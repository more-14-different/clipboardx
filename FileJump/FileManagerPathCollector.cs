using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace ClipboardManager;

/// <summary>
/// 枚举资源管理器 / Total Commander / XYplorer / Directory Opus 等窗口的路径；
/// 思路对齐 QuickSwitch（<see href="https://github.com/gepruts/QuickSwitch"/>）的 ShowMenu / Get_Zfolder。
/// 另对 FreeCommander / Double Commander / Q-Dir / OneCommander / Multi Commander / Tablacus / xplorer² 等无公开消息 API 的窗口，
/// 在进程白名单内通过有限的 UI Automation 扫描提取可验证的本地目录路径（与社区脚本常见做法一致，弱于专用协议）。
/// </summary>
internal static class FileManagerPathCollector
{
    private const int TcMsg = 1075;
    private const int TcmCopySrcPathToClip = 2029;
    private const int TcmCopyTrgPathToClip = 2030;
    private const nint XyCopyDataId = 0x400001;
    private const int ShellExplorerEntriesCacheMs = 15000;
    private const int ShellExplorerEntriesQuickStaleCacheMs = 60000;

    /// <summary>其它白名单管理器：单路径、控节点数以降低 UIA 开销。</summary>
    private const int AlternateUiMaxNodesSingle = 400;

    /// <summary>Q-Dir 四格：多路径；采满即停。</summary>
    private const int QDirMaxDistinctPaths = 6;

    /// <summary>Q-Dir 仅在 Edit/Combo 快速通道后仍不足时再走的浅层 BFS 上限。</summary>
    private const int QDirFallbackMaxNodes = 160;

    private static readonly Condition s_editOrComboCondition = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));

    private static readonly object s_shellExplorerEntriesCacheLock = new();
    private static readonly object s_shellExplorerEntriesEnumerateLock = new();
    private static List<ShellExplorerWindowEntry>? s_shellExplorerEntriesCache;
    private static long s_shellExplorerEntriesCacheTick;

    /// <summary>对 THESE 进程在类名未识别时走 UIA 弱匹配（exe 主名无扩展、不区分大小写）。</summary>
    private static readonly HashSet<string> AlternateUiPathProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "freecommander",
        "doublecmd",
        "onecommander",
        "multicommander",
        "te64",
        "te32",
        "xplorer2",
        "speedcommander",
        "nomadnet",
        "files",
        "winnc",
        "fman",
    };

    private static List<IntPtr> GetTopLevelZOrderTopFirst()
    {
        var r = new List<IntPtr>();
        for (var h = Win32.GetTopWindow(IntPtr.Zero);
             h != IntPtr.Zero;
             h = Win32.GetWindow(h, Win32.GW_HWNDNEXT))
        {
            if (Win32.IsWindow(h) && Win32.IsWindowVisible(h))
                r.Add(h);
        }
        return r;
    }

    /// <summary>与 QuickSwitch Get_Zfolder 相同：对话框在 Z 序中向下偏移 <paramref name="zDelta"/> 个窗口后尝试取路径（默认 2：对话框→宿主→文件管理器）。</summary>
    public static string? TryGetZOrderLinkedFolder(IntPtr dialogHwnd, int zDelta = 2)
    {
        if (dialogHwnd == IntPtr.Zero || zDelta < 1) return null;
        var z = GetTopLevelZOrderTopFirst();
        var idx = z.IndexOf(dialogHwnd);
        if (idx < 0) return null;
        var j = idx + zDelta;
        if (j >= z.Count) return null;
        return TryGetFolderForManagerHwnd(z[j]);
    }

    /// <summary>
    /// 对任意前台窗口尝试提取其所属文件管理器当前路径；若传入的是子窗口，则自动提升到顶层窗口。
    /// 仅对资源管理器 / TC / XYplorer / Q-Dir 等受支持的文件管理器返回路径。
    /// </summary>
    public static string? TryGetFolderForWindow(IntPtr hwnd, bool fresh = false)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return null;
        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        if (fresh)
        {
            // 绕过缓存直接读取 Explorer 路径
            var cls = Win32.GetWindowClassName(root);
            if (cls is "CabinetWClass" or "ExploreWClass")
                return TryGetExplorerPathForHwndFresh(root);
        }
        return TryGetFolderForManagerHwnd(root);
    }

    /// <summary>无缓存地读取 Explorer 窗口当前路径（用于轮询检测路径变化）。</summary>
    private static string? TryGetExplorerPathForHwndFresh(IntPtr explorerFrameHwnd)
    {
        if (explorerFrameHwnd == IntPtr.Zero) return null;
        var entries = TryEnumerateShellExplorerWindowsUncached();
        var (comPath, comMatchScore) = MatchBestComPathForExplorerFrameWithScore(explorerFrameHwnd, entries);

        string? uiaPath = null;
        if (comPath == null || comMatchScore < 4)
        {
            try
            {
                if (FileDialogJumpHelper.TryReadCurrentFolder(explorerFrameHwnd, out var jumpStyle, relaxed: comPath == null)
                    && !string.IsNullOrEmpty(jumpStyle))
                    uiaPath = jumpStyle;
            }
            catch { }
        }

        try { return MergeExplorerComPathWithUiPath(comPath, uiaPath); }
        catch { return uiaPath ?? comPath; }
    }

    /// <summary>按 Z 序遍历顶层窗口，收集各文件管理器当前路径；末尾可附加「常用路径」。</summary>
    /// <param name="skipAlternateUiAutomation">为 true 时跳过白名单第三方管理器的 UIA 树扫描（可快一个数量级），用于先弹出跳转列表再异步补全。</param>
    /// <param name="stopAfterCandidateCount">大于 0 时，在「去重后的候选条数」达到该值后不再遍历剩余顶层窗口（用于快速先开列表，完整列表由后续全量采集补全）。</param>
    /// <param name="shouldAbort">若返回 true 则立即停止顶层窗口遍历（用于采集世代过期时中止长循环）。</param>
    /// <param name="recentFolders">最近确认的目录（最多 3 条）；优先于单独的 <paramref name="memoryFolder"/>。</param>
    public static List<FileJumpCandidate> CollectCandidates(IntPtr dialogHwnd, string? memoryFolder, int zDelta = 2,
        bool skipAlternateUiAutomation = false, int stopAfterCandidateCount = 0, Func<bool>? shouldAbort = null,
        IReadOnlyList<string>? recentFolders = null)
    {
        var swTotal = Stopwatch.StartNew();
        var swStage = Stopwatch.StartNew();
        var slowStages = new List<string>();
        var addedCount = 0;
        var directoryCheckCount = 0;
        long directoryCheckMs = 0;
        var scannedTopLevel = 0;
        var explorerWindowCount = 0;
        var explorerResolveCount = 0;
        long shellEnumMs = 0;
        var shellEnumCacheHit = false;
        long explorerResolveMs = 0;
        long alternateUiMs = 0;
        var alternateUiCount = 0;
        var aborted = false;
        var topCap = -1;
        var exeByPid = new Dictionary<uint, string>();
        var list = new List<FileJumpCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void RecordSlowStage(string name, long elapsedMs, long thresholdMs, string detail = "")
        {
            if (elapsedMs < thresholdMs) return;
            slowStages.Add(string.IsNullOrEmpty(detail)
                ? $"{name}={elapsedMs}ms"
                : $"{name}={elapsedMs}ms({detail})");
        }

        void LogSummary(string exitReason)
        {
            swTotal.Stop();
            if (swTotal.ElapsedMilliseconds < 80 && slowStages.Count == 0) return;
            ClipboardDiagnosticsLog.Write(
                "filejump.perf collect_candidates " +
                $"elapsedMs={swTotal.ElapsedMilliseconds} exit={exitReason} hwnd=0x{dialogHwnd.ToInt64():X} " +
                $"count={list.Count} added={addedCount} scanned={scannedTopLevel} " +
                $"topCap={topCap} " +
                $"skipAlt={skipAlternateUiAutomation} stopAfter={stopAfterCandidateCount} " +
                $"explorerWindows={explorerWindowCount} explorerResolve={explorerResolveCount} " +
                $"shellEnumMs={shellEnumMs} shellCache={shellEnumCacheHit} explorerResolveMs={explorerResolveMs} " +
                $"dirChecks={directoryCheckCount} dirMs={directoryCheckMs} altCount={alternateUiCount} altMs={alternateUiMs} " +
                $"aborted={aborted} slow=[{string.Join("; ", slowStages)}]");
        }

        void Add(string label, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var swDir = Stopwatch.StartNew();
                directoryCheckCount++;
                var exists = Directory.Exists(path);
                swDir.Stop();
                directoryCheckMs += swDir.ElapsedMilliseconds;
                RecordSlowStage("directory_exists", swDir.ElapsedMilliseconds, 25, label);
                if (!exists) return;
                var n = Path.GetFullPath(path);
                if (!seen.Add(n)) return;
                list.Add(new FileJumpCandidate(label, n));
                addedCount++;
            }
            catch { /* ignore */ }
        }

        void AppendFavoriteFolders()
        {
            if (recentFolders != null && recentFolders.Count > 0)
            {
                var idx = 1;
                foreach (var raw in recentFolders)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;
                    Add($"常用路径{idx}", raw.Trim());
                    idx++;
                }

                return;
            }

            if (!string.IsNullOrWhiteSpace(memoryFolder))
                Add("常用路径1", memoryFolder.Trim());
        }

        swStage.Restart();
        if (TryGetZOrderLinkedFolder(dialogHwnd, zDelta) is { } zHint)
            Add("Z 序推测", zHint);
        RecordSlowStage("zorder_hint", swStage.ElapsedMilliseconds, 25);

        if (stopAfterCandidateCount > 0 && list.Count >= stopAfterCandidateCount)
        {
            AppendFavoriteFolders();
            LogSummary("stop_after_zorder");
            return list;
        }

        swStage.Restart();
        var topLevel = GetTopLevelZOrderTopFirst();
        RecordSlowStage("top_level_enum", swStage.ElapsedMilliseconds, 25, $"count={topLevel.Count}");
        var scanCap = topLevel.Count;
        if (stopAfterCandidateCount > 0)
            scanCap = Math.Min(scanCap, 72);
        topCap = scanCap;

        List<ShellExplorerWindowEntry>? shellExplorerEntries = null;

        string? opusXml = null;
        for (var wi = 0; wi < scanCap; wi++)
        {
            if (shouldAbort?.Invoke() == true)
            {
                aborted = true;
                break;
            }

            scannedTopLevel++;
            var h = topLevel[wi];
            var cls = Win32.GetWindowClassName(h);
            switch (cls)
            {
                case "TTOTAL_CMD":
                    if (TryTotalCommanderPathFromClip(h, TcmCopySrcPathToClip, out var tc1))
                        Add("Total Commander (源)", tc1);
                    if (TryTotalCommanderPathFromClip(h, TcmCopyTrgPathToClip, out var tc2))
                        Add("Total Commander (目标)", tc2);
                    break;
                case "ThunderRT6FormDC":
                    if (TryGetProcessImagePath(h, exeByPid, out var xypExe) && 
                        Path.GetFileNameWithoutExtension(xypExe).Equals("xyplorer", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryXyplorerPathFromClip(h, "::copytext get('path', a);", out var xya))
                            Add("XYplorer", xya);
                    }
                    break;
                case "dopus.lister":
                    opusXml ??= TryRunDopusInfoXml(h);
                    foreach (var (pl, pp) in ParseDopusListerPaths(opusXml, h))
                        Add(pl, pp);
                    break;
                case "CabinetWClass":
                case "ExploreWClass":
                {
                    if (shellExplorerEntries == null)
                    {
                        swStage.Restart();
                        shellExplorerEntries = TryEnumerateShellExplorerWindows(
                            out var cacheHit,
                            allowBlockingRefresh: true,
                            allowStaleCache: stopAfterCandidateCount > 0);
                        shellEnumMs += swStage.ElapsedMilliseconds;
                        shellEnumCacheHit = cacheHit;
                        explorerWindowCount = shellExplorerEntries.Count;
                        RecordSlowStage(
                            cacheHit ? "shell_windows_enum_cache" : "shell_windows_enum",
                            swStage.ElapsedMilliseconds,
                            cacheHit ? 8 : 40,
                            $"count={shellExplorerEntries.Count}");
                    }

                    swStage.Restart();
                    explorerResolveCount++;
                    if (TryGetExplorerPathForHwnd(h, shellExplorerEntries) is { } ex)
                        Add("资源管理器", ex);
                    explorerResolveMs += swStage.ElapsedMilliseconds;
                    RecordSlowStage("explorer_resolve", swStage.ElapsedMilliseconds, 50,
                        $"hwnd=0x{h.ToInt64():X}");
                    break;
                }
                default:
                    if (skipAlternateUiAutomation) break;
                    if (!TryGetProcessImagePath(h, exeByPid, out var altExe)) break;
                    {
                        var altProc = Path.GetFileNameWithoutExtension(altExe);
                        if (!ShouldUseAlternateUiAutomation(altProc)) break;
                        var altLabel = AlternateManagerDisplayLabel(altProc, altExe);
                        if (altProc.StartsWith("q-dir", StringComparison.OrdinalIgnoreCase))
                        {
                            swStage.Restart();
                            alternateUiCount++;
                            foreach (var p in CollectQDirFolderPathsFromAutomation(h))
                                Add(altLabel, p);
                            alternateUiMs += swStage.ElapsedMilliseconds;
                            RecordSlowStage("alternate_qdir_uia", swStage.ElapsedMilliseconds, 60,
                                $"proc={altProc}");
                        }
                        else
                        {
                            swStage.Restart();
                            alternateUiCount++;
                            if (TryFindBestFolderPathInAutomationTree(h, out var alt))
                                Add(altLabel, alt);
                            alternateUiMs += swStage.ElapsedMilliseconds;
                            RecordSlowStage("alternate_uia", swStage.ElapsedMilliseconds, 60,
                                $"proc={altProc}");
                        }
                    }
                    break;
            }

            if (stopAfterCandidateCount > 0 && list.Count >= stopAfterCandidateCount)
                break;
        }

        swStage.Restart();
        AppendFavoriteFolders();
        RecordSlowStage("append_favorites", swStage.ElapsedMilliseconds, 25,
            $"recent={(recentFolders?.Count ?? 0)}");

        LogSummary("complete");
        return list;
    }

    private static string? TryGetFolderForManagerHwnd(IntPtr h)
    {
        var cls = Win32.GetWindowClassName(h);
        if (cls == "TTOTAL_CMD")
            return TryTotalCommanderPathFromClip(h, TcmCopySrcPathToClip, out var p) ? p : null;
        if (cls == "ThunderRT6FormDC")
        {
            if (TryGetProcessImagePath(h, null, out var xypExe) && Path.GetFileNameWithoutExtension(xypExe).Equals("xyplorer", StringComparison.OrdinalIgnoreCase))
                return TryXyplorerPathFromClip(h, "::copytext get('path', a);", out var x) ? x : null;
            return null;
        }
        if (cls == "CabinetWClass" || cls == "ExploreWClass")
            return TryGetExplorerPathForHwnd(h);
        if (cls == "dopus.lister")
            return ParseDopusListerPaths(TryRunDopusInfoXml(h), h).Select(t => t.path).FirstOrDefault();
        
        return TryGetFolderForAlternateUiManager(h);
    }

    private static bool ShouldUseAlternateUiAutomation(string procBaseName)
    {
        if (AlternateUiPathProcesses.Contains(procBaseName)) return true;
        // Microsoft Store「文件」等可能为 Files、Files!App 等变体
        if (procBaseName.StartsWith("files", StringComparison.OrdinalIgnoreCase)) return true;
        // Q-Dir.exe、Q-Dir_x64 等（SoftwareOK）
        if (procBaseName.StartsWith("q-dir", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>无专用接口时：白名单进程 + 浅层 UIA 抓取已是真实目录的路径字符串。</summary>
    private static string? TryGetFolderForAlternateUiManager(IntPtr h)
    {
        if (!TryGetProcessImagePath(h, out var exe)) return null;
        var proc = Path.GetFileNameWithoutExtension(exe);
        if (!ShouldUseAlternateUiAutomation(proc)) return null;
        return TryFindBestFolderPathInAutomationTree(h, out var path) ? path : null;
    }

    private static string AlternateManagerDisplayLabel(string procFileBaseName, string exeFullPath)
    {
        var pl = procFileBaseName.ToLowerInvariant();
        return pl switch
        {
            "freecommander" => "FreeCommander",
            "doublecmd" => "Double Commander",
            "onecommander" => "OneCommander",
            "multicommander" => "Multi Commander",
            "te64" or "te32" => "Tablacus Explorer",
            "xplorer2" => "xplorer²",
            "speedcommander" => "SpeedCommander",
            "nomadnet" => "Nomad .NET",
            "winnc" => "WinNc",
            "fman" => "fman",
            var x when x.StartsWith("files", StringComparison.OrdinalIgnoreCase) => "Files",
            var x when x.StartsWith("q-dir", StringComparison.OrdinalIgnoreCase) => "Q-Dir",
            _ => Path.GetFileNameWithoutExtension(exeFullPath),
        };
    }

    private static bool TryFindBestFolderPathInAutomationTree(IntPtr hwnd, out string best)
    {
        best = "";
        var acc = "";
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return false;

            var q = new Queue<AutomationElement>();
            q.Enqueue(root);
            for (var seen = 0; q.Count > 0 && seen < AlternateUiMaxNodesSingle; seen++)
            {
                var el = q.Dequeue();
                try
                {
                    foreach (AutomationElement c in el.FindAll(TreeScope.Children, Condition.TrueCondition))
                        q.Enqueue(c);
                }
                catch { /* ignore */ }

                ForEachUiStringOnElement(el, includeAutomationId: false, s => TryTakeLongerExistingDir(s, ref acc));
            }
        }
        catch
        {
            return false;
        }

        best = acc;
        return acc.Length > 0;
    }

    /// <summary>Q-Dir 多窗格：优先扫地址栏 Edit/Combo（UIA 原生枚举，避免整窗逐子结点 BFS）；不足再浅层补扫。</summary>
    private static List<string> CollectQDirFolderPathsFromAutomation(IntPtr hwnd)
    {
        var sink = new List<string>(6);
        var pathSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return sink;

            AutomationElementCollection? edits = null;
            try
            {
                edits = root.FindAll(TreeScope.Descendants, s_editOrComboCondition);
            }
            catch { /* ignore */ }

            if (edits != null)
            {
                foreach (AutomationElement el in edits)
                {
                    ForEachUiStringOnElement(el, includeAutomationId: false, s =>
                    {
                        AddDistinctFolderPathsFromText(s, pathSeen, sink);
                    });
                    if (sink.Count >= QDirMaxDistinctPaths) return sink;
                }
            }

            if (sink.Count >= 3) return sink;

            QDirFallbackShallowBfs(root, pathSeen, sink);
        }
        catch { /* ignore */ }

        return sink;
    }

    private static void QDirFallbackShallowBfs(AutomationElement root, HashSet<string> pathSeen, List<string> sink)
    {
        var q = new Queue<AutomationElement>();
        q.Enqueue(root);
        for (var seen = 0; q.Count > 0 && seen < QDirFallbackMaxNodes && sink.Count < QDirMaxDistinctPaths; seen++)
        {
            var el = q.Dequeue();
            try
            {
                foreach (AutomationElement c in el.FindAll(TreeScope.Children, Condition.TrueCondition))
                    q.Enqueue(c);
            }
            catch { /* ignore */ }

            ForEachUiStringOnElement(el, includeAutomationId: false, s =>
                AddDistinctFolderPathsFromText(s, pathSeen, sink));
        }
    }

    private static void ForEachUiStringOnElement(AutomationElement el, bool includeAutomationId, Action<string?> onText)
    {
        try
        {
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj))
                onText(((ValuePattern)vpObj).Current.Value);
        }
        catch { /* ignore */ }

        try
        {
            if (el.TryGetCurrentPattern(TextPattern.Pattern, out var tpObj))
                onText(((TextPattern)tpObj).DocumentRange.GetText(-1));
        }
        catch { /* ignore */ }

        try
        {
            onText(el.Current.Name);
        }
        catch { /* ignore */ }

        try
        {
            onText(el.Current.HelpText);
        }
        catch { /* ignore */ }

        if (!includeAutomationId) return;

        try
        {
            onText(el.Current.AutomationId);
        }
        catch { /* ignore */ }
    }

    private static void AddDistinctFolderPathsFromText(string? text, HashSet<string> pathSeen, List<string> sink)
    {
        if (string.IsNullOrEmpty(text) || sink.Count >= QDirMaxDistinctPaths) return;
        void TryAdd(string? normRaw)
        {
            if (string.IsNullOrEmpty(normRaw) || sink.Count >= QDirMaxDistinctPaths) return;
            string norm;
            try
            {
                norm = Path.GetFullPath(normRaw.TrimEnd('\\', '/'));
            }
            catch
            {
                return;
            }
            if (!Directory.Exists(norm)) return;
            if (!pathSeen.Add(norm)) return;
            sink.Add(norm);
        }

        if (FileDialogJumpHelper.TryNormalizeToExistingDirectory(text, out var n1))
            TryAdd(n1);
        if (sink.Count >= QDirMaxDistinctPaths) return;
        if (HasBreadcrumbArrow(text)
            && FileDialogJumpHelper.TryWpsBreadcrumbTextToFolder(text, out var n2))
            TryAdd(n2);
    }

    private static bool HasBreadcrumbArrow(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains('>') || text.Contains('＞') || text.Contains('›');
    }

    private static void TryTakeLongerExistingDir(string? text, ref string best)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (FileDialogJumpHelper.TryNormalizeToExistingDirectory(text, out var norm) && norm.Length > best.Length)
            best = norm;
        if (!HasBreadcrumbArrow(text)) return;
        if (FileDialogJumpHelper.TryWpsBreadcrumbTextToFolder(text, out var crumb) && crumb.Length > best.Length)
            best = crumb;
    }

    private static bool TryTotalCommanderPathFromClip(IntPtr tcHwnd, int commandId, out string path)
    {
        path = "";
        ClipboardGate.Enter();
        try
        {
            string? backup = null;
            try
            {
                if (System.Windows.Clipboard.ContainsText()) backup = System.Windows.Clipboard.GetText();
            }
            catch { /* ignore */ }

            try { System.Windows.Clipboard.Clear(); } catch { /* ignore */ }

            Win32.SendMessage(tcHwnd, TcMsg, (IntPtr)commandId, IntPtr.Zero);
            Thread.Sleep(90);
            try
            {
                path = System.Windows.Clipboard.GetText()?.Trim() ?? "";
            }
            catch { path = ""; }

            try
            {
                if (backup != null) System.Windows.Clipboard.SetText(backup);
                else System.Windows.Clipboard.Clear();
            }
            catch { /* ignore */ }

            return Directory.Exists(path);
        }
        finally
        {
            ClipboardGate.Exit();
        }
    }

    private static bool TryXyplorerPathFromClip(IntPtr xyHwnd, string script, out string path)
    {
        path = "";
        ClipboardGate.Enter();
        try
        {
            SendXyplorerCopyData(xyHwnd, script);
            Thread.Sleep(120);
            try
            {
                path = System.Windows.Clipboard.GetText()?.Trim() ?? "";
            }
            catch { path = ""; }

            return Directory.Exists(path);
        }
        finally
        {
            ClipboardGate.Exit();
        }
    }

    private static void SendXyplorerCopyData(IntPtr xyHwnd, string message)
    {
        var bytes = Encoding.Unicode.GetBytes(message);
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            var cds = new Win32.COPYDATASTRUCT
            {
                dwData = (IntPtr)XyCopyDataId,
                cbData = bytes.Length,
                lpData = ptr
            };
            Win32.SendMessage(xyHwnd, Win32.WM_COPYDATA, IntPtr.Zero, ref cds);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>单次 <see cref="CollectCandidates"/> 内复用：避免每个资源管理器窗口都全量遍历 Shell.Application.Windows。</summary>
    private readonly struct ShellExplorerWindowEntry
    {
        public ShellExplorerWindowEntry(IntPtr reportedHwnd, string path)
        {
            ReportedHwnd = reportedHwnd;
            Path = path;
        }

        public IntPtr ReportedHwnd { get; }
        public string Path { get; }
    }

    private static List<ShellExplorerWindowEntry> TryEnumerateShellExplorerWindows()
        => TryEnumerateShellExplorerWindows(out _, allowBlockingRefresh: true, allowStaleCache: false);

    private static List<ShellExplorerWindowEntry> TryEnumerateShellExplorerWindows(
        out bool cacheHit,
        bool allowBlockingRefresh,
        bool allowStaleCache)
    {
        cacheHit = false;
        if (TryGetShellExplorerEntriesCache(ShellExplorerEntriesCacheMs, out var cached))
        {
            cacheHit = true;
            return cached;
        }

        if (allowStaleCache
            && TryGetShellExplorerEntriesCache(ShellExplorerEntriesQuickStaleCacheMs, out cached))
        {
            cacheHit = true;
            return cached;
        }

        if (!allowBlockingRefresh)
            return new List<ShellExplorerWindowEntry>();

        lock (s_shellExplorerEntriesEnumerateLock)
        {
            if (TryGetShellExplorerEntriesCache(ShellExplorerEntriesCacheMs, out cached)
                || (allowStaleCache
                    && TryGetShellExplorerEntriesCache(ShellExplorerEntriesQuickStaleCacheMs, out cached)))
            {
                cacheHit = true;
                return cached;
            }

            var fresh = TryEnumerateShellExplorerWindowsUncached();
            lock (s_shellExplorerEntriesCacheLock)
            {
                s_shellExplorerEntriesCache = fresh;
                s_shellExplorerEntriesCacheTick = Environment.TickCount64;
            }

            return new List<ShellExplorerWindowEntry>(fresh);
        }
    }

    private static bool TryGetShellExplorerEntriesCache(int maxAgeMs, out List<ShellExplorerWindowEntry> entries)
    {
        entries = new List<ShellExplorerWindowEntry>();
        var now = Environment.TickCount64;
        lock (s_shellExplorerEntriesCacheLock)
        {
            if (s_shellExplorerEntriesCache == null
                || now - s_shellExplorerEntriesCacheTick < 0
                || now - s_shellExplorerEntriesCacheTick > maxAgeMs)
                return false;

            entries = new List<ShellExplorerWindowEntry>(s_shellExplorerEntriesCache);
            return true;
        }
    }

    private static List<ShellExplorerWindowEntry> TryEnumerateShellExplorerWindowsUncached()
    {
        var r = new List<ShellExplorerWindowEntry>();
        Type? t = Type.GetTypeFromProgID("Shell.Application");
        object? shell = null;
        object? windows = null;
        try
        {
            if (t == null) return r;
            shell = Activator.CreateInstance(t);
            if (shell == null) return r;
            windows = TryInvokeShellWindows(shell);
            if (windows == null) return r;
            var wt = windows.GetType();
            var countObj = wt.InvokeMember("Count", BindingFlags.GetProperty, null, windows, null);
            var count = Convert.ToInt32(countObj);
            for (var i = 0; i < count; i++)
            {
                object? win = null;
                try
                {
                    win = wt.InvokeMember("Item", BindingFlags.InvokeMethod, null, windows, new object[] { i });
                    if (win == null) continue;
                    var comHwnd = TryGetInternetExplorerHwnd(win);
                    var path = ReadShellWindowPath(win);
                    if (string.IsNullOrEmpty(path)) continue;
                    r.Add(new ShellExplorerWindowEntry(comHwnd, path));
                }
                finally
                {
                    if (win != null) Marshal.ReleaseComObject(win);
                }
            }
        }
        catch { /* COM 失败 */ }
        finally
        {
            if (windows != null) Marshal.ReleaseComObject(windows);
            if (shell != null) Marshal.ReleaseComObject(shell);
        }

        return r;
    }

    /// <returns>最佳 COM 路径与对应匹配分；无有效项时 bestScore 为 <see cref="int.MinValue"/>。</returns>
    private static (string? path, int bestScore) MatchBestComPathForExplorerFrameWithScore(IntPtr explorerFrameHwnd,
        List<ShellExplorerWindowEntry> entries)
    {
        if (entries.Count == 0) return (null, int.MinValue);
        var bestScore = int.MinValue;
        string? comPath = null;
        foreach (var e in entries)
        {
            var score = ExplorerComMatchScore(explorerFrameHwnd, e.ReportedHwnd);
            if (score < 0 || score < bestScore) continue;
            if (string.IsNullOrEmpty(e.Path)) continue;
            if (score > bestScore)
            {
                bestScore = score;
                comPath = e.Path;
            }
            else if (score == bestScore
                     && (string.IsNullOrEmpty(comPath) || e.Path.Length > comPath!.Length))
            {
                comPath = e.Path;
            }
        }

        return (comPath, bestScore);
    }

    /// <summary>
    /// 通过 Shell.Application.Windows 取资源管理器路径；必要时用 UIA 与 COM 合并（Win11 多标签等）。
    /// </summary>
    /// <param name="prebuiltShellEntries">非 null 时复用已枚举的 Shell 窗口（同一次 CollectCandidates 内多次 Cabinet 窗口只需一次 COM 全量扫描）。</param>
    private static string? TryGetExplorerPathForHwnd(IntPtr explorerFrameHwnd,
        List<ShellExplorerWindowEntry>? prebuiltShellEntries = null)
    {
        if (explorerFrameHwnd == IntPtr.Zero) return null;
        var (comPath, comMatchScore) = prebuiltShellEntries != null
            ? MatchBestComPathForExplorerFrameWithScore(explorerFrameHwnd, prebuiltShellEntries)
            : MatchBestComPathForExplorerFrameWithScore(explorerFrameHwnd, TryEnumerateShellExplorerWindows());

        string? uiaPath = null;
        // Shell COM 快而 relaxed UIA 对 Explorer（Classify=None）可走满 500 节点 BFS；多窗叠加易达秒级。
        // COM 与 Shell 窗口 HWND 完全一致（分=4）时再扫整树多为重复。
        if (comPath == null || comMatchScore < 4)
        {
            var relaxedUia = comPath == null;
            if (comPath != null && comMatchScore < 4)
                relaxedUia = false;
            try
            {
                if (FileDialogJumpHelper.TryReadCurrentFolder(explorerFrameHwnd, out var jumpStyle, relaxed: relaxedUia)
                    && !string.IsNullOrEmpty(jumpStyle))
                    uiaPath = jumpStyle;
            }
            catch { /* ignore */ }
        }

        // Shell.Document 与前台地址栏在 Win11 上可能不一致；曾提早 return comPath 导致界面在 D:\gn 仍显示 C:\。
        try
        {
            return MergeExplorerComPathWithUiPath(comPath, uiaPath);
        }
        catch
        {
            if (!string.IsNullOrEmpty(uiaPath)) return uiaPath;
            return comPath;
        }
    }

    /// <summary>COM 路径与 UIA（与文件夹跳转同源）合并：盘符根误报、多标签错项时以界面为准。</summary>
    private static string? MergeExplorerComPathWithUiPath(string? comPath, string? uiaPath)
    {
        if (string.IsNullOrEmpty(comPath)) return string.IsNullOrEmpty(uiaPath) ? null : uiaPath;
        if (string.IsNullOrEmpty(uiaPath)) return comPath;

        string c, u;
        try
        {
            c = Path.GetFullPath(comPath.Trim().TrimEnd('\\', '/'));
            u = Path.GetFullPath(uiaPath.Trim().TrimEnd('\\', '/'));
        }
        catch
        {
            return uiaPath;
        }

        if (string.Equals(c, u, StringComparison.OrdinalIgnoreCase))
            return comPath;

        if (u.StartsWith(c + "\\", StringComparison.OrdinalIgnoreCase))
            return uiaPath;
        if (c.StartsWith(u + "\\", StringComparison.OrdinalIgnoreCase))
            return comPath;

        if (ExplorerPathIsDriveLetterRootOnly(c) && !ExplorerPathIsDriveLetterRootOnly(u))
            return uiaPath;
        if (ExplorerPathIsDriveLetterRootOnly(u) && !ExplorerPathIsDriveLetterRootOnly(c))
            return comPath;

        return uiaPath;
    }

    private static bool ExplorerPathIsDriveLetterRootOnly(string normalizedFullPath)
    {
        try
        {
            var root = Path.GetPathRoot(normalizedFullPath);
            if (string.IsNullOrEmpty(root)) return false;
            return string.Equals(
                normalizedFullPath.TrimEnd('\\', '/'),
                root.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static object? TryInvokeShellWindows(object shell)
    {
        var shellType = shell.GetType();
        try
        {
            return shellType.InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null);
        }
        catch { /* 部分宿主上 Windows 为属性 */ }

        try
        {
            return shellType.InvokeMember("Windows", BindingFlags.GetProperty, null, shell, null);
        }
        catch { return null; }
    }

    private static IntPtr TryGetInternetExplorerHwnd(object win)
    {
        var t = win.GetType();
        foreach (var name in new[] { "HWND", "Hwnd" })
        {
            try
            {
                var v = t.InvokeMember(name, BindingFlags.GetProperty | BindingFlags.IgnoreCase, null, win, null);
                if (v == null) continue;
                var p = ComObjectToIntPtr(v);
                if (p != IntPtr.Zero) return p;
            }
            catch { /* ignore */ }
        }
        return IntPtr.Zero;
    }

    private static IntPtr ComObjectToIntPtr(object v)
    {
        if (v is IntPtr p) return p;
        try
        {
            return new IntPtr(Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <returns>越高越可信；&lt;0 表示不应视为同一资源管理器窗口。</returns>
    private static int ExplorerComMatchScore(IntPtr explorerFrameHwnd, IntPtr shellReportedHwnd)
    {
        if (explorerFrameHwnd == IntPtr.Zero || shellReportedHwnd == IntPtr.Zero) return -1;
        if (explorerFrameHwnd == shellReportedHwnd) return 4;
        if (Win32.IsChild(explorerFrameHwnd, shellReportedHwnd)) return 3;
        for (var w = shellReportedHwnd; w != IntPtr.Zero; w = Win32.GetParent(w))
        {
            if (w == explorerFrameHwnd) return 2;
        }
        if (Win32.GetAncestor(shellReportedHwnd, Win32.GA_ROOT) == explorerFrameHwnd) return 1;
        var frameRoot = Win32.GetAncestor(explorerFrameHwnd, Win32.GA_ROOT);
        var shellRoot = Win32.GetAncestor(shellReportedHwnd, Win32.GA_ROOT);
        if (frameRoot != IntPtr.Zero && frameRoot == shellRoot) return 0;
        return -1;
    }

    private static string? ReadShellWindowPath(object win)
    {
        object? doc = null;
        object? folder = null;
        object? self = null;
        try
        {
            doc = win.GetType().InvokeMember("Document", BindingFlags.GetProperty, null, win, null);
            if (doc == null) return null;
            folder = doc.GetType().InvokeMember("Folder", BindingFlags.GetProperty, null, doc, null);
            if (folder == null) return null;
            self = folder.GetType().InvokeMember("Self", BindingFlags.GetProperty, null, folder, null);
            if (self == null) return null;
            var path = self.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, self, null);
            return path?.ToString();
        }
        catch { return null; }
        finally
        {
            if (self != null) Marshal.ReleaseComObject(self);
            if (folder != null) Marshal.ReleaseComObject(folder);
            if (doc != null) Marshal.ReleaseComObject(doc);
        }
    }

    private static string? TryRunDopusInfoXml(IntPtr anyListerHwnd)
    {
        if (!TryGetProcessImagePath(anyListerHwnd, out var exe)) return null;
        var dir = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(dir)) return null;
        var rt = Path.Combine(dir, "dopusrt.exe");
        if (!File.Exists(rt)) return null;

        var temp = Path.Combine(Path.GetTempPath(), "ClipboardX-dopus-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = rt,
                ArgumentList = { "/info", temp, "paths" },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(5000);
            if (!File.Exists(temp)) return null;
            return File.ReadAllText(temp);
        }
        catch { return null; }
        finally
        {
            try { File.Delete(temp); } catch { /* ignore */ }
        }
    }

    private static IEnumerable<(string label, string path)> ParseDopusListerPaths(string? xml, IntPtr listerHwnd)
    {
        if (string.IsNullOrEmpty(xml)) yield break;
        var idVariants = new HashSet<string>(StringComparer.Ordinal);
        idVariants.Add(((nint)listerHwnd).ToString());
        idVariants.Add(unchecked((uint)(nint)listerHwnd).ToString());
        foreach (var (state, label) in new[] { ("1", "Directory Opus (活动)"), ("2", "Directory Opus (被动)") })
        {
            foreach (var id in idVariants)
            {
                var pattern =
                    $@"(?is)lister\s*=\s*""{Regex.Escape(id)}""[^>]*tab_state\s*=\s*""{state}""[^>]*>\s*([^<]+?)\s*</path>";
                var m = Regex.Match(xml, pattern);
                if (m.Success && Directory.Exists(m.Groups[1].Value.Trim()))
                {
                    yield return (label, m.Groups[1].Value.Trim());
                    break;
                }
            }
        }
    }

    private static bool TryGetProcessImagePath(IntPtr hwnd, out string path)
        => TryGetProcessImagePath(hwnd, null, out path);

    /// <summary>单次 <see cref="CollectCandidates"/> 内复用 PID→exe，避免对大量顶层窗口重复 OpenProcess。</summary>
    private static bool TryGetProcessImagePath(IntPtr hwnd, Dictionary<uint, string>? exeByPid, out string path)
    {
        path = "";
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (exeByPid != null && exeByPid.TryGetValue(pid, out var cached))
        {
            path = cached;
            return !string.IsNullOrEmpty(path);
        }

        var hProc = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero)
        {
            exeByPid?.TryAdd(pid, "");
            return false;
        }

        try
        {
            var sb = new StringBuilder(1024);
            var ok = Win32.GetModuleFileNameEx(hProc, IntPtr.Zero, sb, sb.Capacity) > 0
                     && File.Exists(path = sb.ToString());
            if (!ok) path = "";
            if (exeByPid != null) exeByPid[pid] = path;
            return ok;
        }
        finally
        {
            Win32.CloseHandle(hProc);
        }
    }

    private sealed class ExplorerCabinetEnumState
    {
        public IntPtr Fg;
        public uint Pid;
        public IntPtr Found;
    }

    private static bool EnumExplorerCabinetCallback(IntPtr hWnd, IntPtr lParam)
    {
        var st = (ExplorerCabinetEnumState)GCHandle.FromIntPtr(lParam).Target!;
        if (hWnd == IntPtr.Zero || !Win32.IsWindowVisible(hWnd)) return true;
        var cls = Win32.GetWindowClassName(hWnd);
        if (!cls.Equals("CabinetWClass", StringComparison.Ordinal)
            && !cls.Equals("ExploreWClass", StringComparison.Ordinal))
            return true;
        Win32.GetWindowThreadProcessId(hWnd, out var p);
        if (p != st.Pid) return true;
        if (hWnd == st.Fg || Win32.IsChild(hWnd, st.Fg))
        {
            st.Found = hWnd;
            return false;
        }

        return true;
    }

    /// <summary>
    /// 父链断裂时：枚举同进程顶层 Cabinet，找到包含前台句柄的框架。
    /// </summary>
    private static IntPtr TryFindExplorerCabinetByEnumContains(IntPtr fg)
    {
        Win32.GetWindowThreadProcessId(fg, out var pid);
        var st = new ExplorerCabinetEnumState { Fg = fg, Pid = pid, Found = IntPtr.Zero };
        var gh = GCHandle.Alloc(st);
        try
        {
            Win32.EnumWindows(EnumExplorerCabinetCallback, GCHandle.ToIntPtr(gh));
            return st.Found;
        }
        finally
        {
            gh.Free();
        }
    }

    /// <summary>
    /// 自任意句柄沿 <see cref="Win32.GetParent"/> 向上查找资源管理器框架。
    /// Win11 新版布局/XAML 岛等场景下父链或 GA_ROOT 可能无法直接命中，再用 <see cref="TryFindExplorerCabinetByEnumContains"/>。
    /// </summary>
    public static IntPtr TryFindExplorerCabinetFrame(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return IntPtr.Zero;
        const int maxHops = 64;
        var w = hwnd;
        for (var i = 0; i < maxHops && w != IntPtr.Zero; i++)
        {
            var cls = Win32.GetWindowClassName(w);
            if (cls.Equals("CabinetWClass", StringComparison.Ordinal)
                || cls.Equals("ExploreWClass", StringComparison.Ordinal))
                return w;
            w = Win32.GetParent(w);
        }

        return TryFindExplorerCabinetByEnumContains(hwnd);
    }

    /// <summary>
    /// 若 <paramref name="foregroundHwnd"/> 所属（父链上）为资源管理器框架，返回当前文件夹路径。
    /// </summary>
    public static string? TryGetExplorerFolderIfForeground(IntPtr foregroundHwnd)
    {
        if (foregroundHwnd == IntPtr.Zero || !Win32.IsWindow(foregroundHwnd)) return null;
        var frame = TryFindExplorerCabinetFrame(foregroundHwnd);
        if (frame == IntPtr.Zero) return null;
        return TryGetExplorerPathForHwnd(frame);
    }
}
