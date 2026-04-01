using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace ClipboardManager;

/// <summary>
/// 识别文件对话框类型（对齐 QuickSwitch 的 SysListView / DirectUI 启发式）、读取与跳转路径。
/// </summary>
internal enum FileDialogKind
{
    None,
    /// <summary>#32770 + Shell 视图，或未检出子类型时的通用处理（地址栏）。</summary>
    ShellDefViewOrGeneral,
    /// <summary>DirectUIHWND + ToolbarWindow32 + Edit（较新通用对话框）。</summary>
    GeneralDirectUi,
    /// <summary>SysListView32 + ToolbarWindow32 + Edit（如部分旧式宿主）。</summary>
    SysListView,
    /// <summary>WPS 办公组件（wps/et/wpp 等）自带的「打开文件 / 另存为」等非 #32770 对话框。</summary>
    WpsCustom,
}

internal static class FileDialogJumpHelper
{
    public static FileDialogKind ClassifyFileDialog(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return FileDialogKind.None;

        // WPS 不使用系统公共 #32770 对话框，须在类名判断之前识别。
        if (IsWpsSuiteFileDialog(hwnd))
            return FileDialogKind.WpsCustom;

        if (!Win32.GetWindowClassName(hwnd).Equals("#32770", StringComparison.Ordinal))
            return FileDialogKind.None;

        var classes = CollectDescendantClassNames(hwnd);
        var hasDirect = classes.Any(c => c.Contains("DirectUIHWND", StringComparison.Ordinal));
        var hasList = classes.Contains("SysListView32", StringComparer.OrdinalIgnoreCase);
        var hasTb = classes.Contains("ToolbarWindow32", StringComparer.OrdinalIgnoreCase);
        var hasEdit = classes.Contains("Edit", StringComparer.OrdinalIgnoreCase);

        if (hasDirect && hasTb && hasEdit) return FileDialogKind.GeneralDirectUi;
        if (hasList && hasTb && hasEdit) return FileDialogKind.SysListView;
        if (ContainsShellDefView(hwnd)) return FileDialogKind.ShellDefViewOrGeneral;
        if (IsFileDialogTitle(Win32.GetWindowText(hwnd))) return FileDialogKind.ShellDefViewOrGeneral;
        return FileDialogKind.None;
    }

    public static bool IsLikelyFileDialog(IntPtr hwnd) => ClassifyFileDialog(hwnd) != FileDialogKind.None;

    /// <summary>进程主模块基名（小写，无扩展名），用于识别 WPS 套件。</summary>
    private static bool TryGetExeBaseNameLower(IntPtr hwnd, out string name)
    {
        name = "";
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return false;
        var h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            var sb = new StringBuilder(1024);
            if (Win32.GetModuleFileNameEx(h, IntPtr.Zero, sb, sb.Capacity) == 0)
                return false;
            name = Path.GetFileNameWithoutExtension(sb.ToString()).ToLowerInvariant();
            return name.Length > 0;
        }
        finally
        {
            Win32.CloseHandle(h);
        }
    }

    private static bool IsWpsSuiteExe(string exeBaseLower) =>
        exeBaseLower is "wps" or "et" or "wpp" or "pdf" or "ksolaunch";

    /// <summary>WPS 自定义打开/保存窗口标题（需与套件进程同时匹配，避免误伤其他「打开」对话框）。</summary>
    private static bool IsWpsFileDialogTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        if (title.Contains("打开文件", StringComparison.Ordinal)) return true;
        if (title.Contains("另存文件", StringComparison.Ordinal)) return true;
        if (title.Contains("保存文件", StringComparison.Ordinal)) return true;
        // 简短标题（部分语言包）
        if (title.Equals("另存为", StringComparison.Ordinal)) return true;
        if (title.Equals("保存", StringComparison.Ordinal)) return true;
        var t = title.ToLowerInvariant();
        if (t.Contains("save as")) return true;
        if (t.Contains("open file")) return true;
        return false;
    }

    private static bool IsWpsSuiteFileDialog(IntPtr hwnd)
    {
        if (!TryGetExeBaseNameLower(hwnd, out var exe) || !IsWpsSuiteExe(exe))
            return false;
        return IsWpsFileDialogTitle(Win32.GetWindowText(hwnd));
    }

    private static HashSet<string> CollectDescendantClassNames(IntPtr root)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(IntPtr h)
        {
            set.Add(Win32.GetWindowClassName(h));
            Win32.EnumChildWindows(h, (c, _) =>
            {
                Walk(c);
                return true;
            }, IntPtr.Zero);
        }
        Walk(root);
        return set;
    }

    private static bool ContainsShellDefView(IntPtr root)
    {
        var stack = new Stack<IntPtr>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var h = stack.Pop();
            if (string.Equals(Win32.GetWindowClassName(h), "SHELLDLL_DefView", StringComparison.Ordinal))
                return true;
            var children = new List<IntPtr>();
            Win32.EnumChildWindows(h, (ch, _) =>
            {
                children.Add(ch);
                return true;
            }, IntPtr.Zero);
            foreach (var c in children) stack.Push(c);
        }
        return false;
    }

    private static bool IsFileDialogTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        var t = title.ToLowerInvariant();
        return title.Contains("打开", StringComparison.Ordinal)
               || title.Contains("另存", StringComparison.Ordinal)
               || title.Contains("保存", StringComparison.Ordinal)
               || t.Contains("open", StringComparison.Ordinal)
               || t.Contains("save", StringComparison.Ordinal)
               || t.Contains("browse", StringComparison.Ordinal);
    }

    public static bool TryReadCurrentFolder(IntPtr hwnd, out string folder)
    {
        folder = "";
        var kind = ClassifyFileDialog(hwnd);
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return false;

            var best = "";
            var q = new Queue<AutomationElement>();
            q.Enqueue(root);
            var seen = 0;
            var maxNodes = kind == FileDialogKind.WpsCustom ? 450 : 150;
            while (q.Count > 0 && seen < maxNodes)
            {
                var el = q.Dequeue();
                seen++;
                try
                {
                    foreach (AutomationElement c in el.FindAll(TreeScope.Children, Condition.TrueCondition))
                        q.Enqueue(c);
                }
                catch { /* ignore */ }

                try
                {
                    if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj))
                    {
                        var v = ((ValuePattern)vpObj).Current.Value;
                        if (TryNormalizeToExistingDirectory(v, out var norm) && norm.Length > best.Length)
                            best = norm;
                    }
                }
                catch { }

                if (kind != FileDialogKind.WpsCustom || best.Length > 0) continue;

                try
                {
                    var name = el.Current.Name;
                    if (TryWpsBreadcrumbTextToFolder(name, out var bc) && bc.Length > best.Length)
                        best = bc;
                }
                catch { }

                try
                {
                    var aid = el.Current.AutomationId;
                    if (!string.IsNullOrEmpty(aid)
                        && TryWpsBreadcrumbTextToFolder(aid, out var bc2) && bc2.Length > best.Length)
                        best = bc2;
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(best))
            {
                folder = best;
                return true;
            }
        }
        catch { }

        if (kind == FileDialogKind.WpsCustom && TryReadWpsBreadcrumbOnly(hwnd, out var wpsFolder))
        {
            folder = wpsFolder;
            return true;
        }

        return false;
    }

    /// <summary>专用于 ValuePattern 未给出完整路径时，仅从名称/AutomationId 中的面包屑推断目录。</summary>
    private static bool TryReadWpsBreadcrumbOnly(IntPtr hwnd, out string folder)
    {
        folder = "";
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return false;
            var best = "";
            var q = new Queue<AutomationElement>();
            q.Enqueue(root);
            for (var seen = 0; q.Count > 0 && seen < 500; seen++)
            {
                var el = q.Dequeue();
                try
                {
                    foreach (AutomationElement c in el.FindAll(TreeScope.Children, Condition.TrueCondition))
                        q.Enqueue(c);
                }
                catch { /* ignore */ }

                try
                {
                    var name = el.Current.Name;
                    if (TryWpsBreadcrumbTextToFolder(name, out var p) && p.Length > best.Length)
                        best = p;
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(best))
            {
                folder = best;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool TryWpsBreadcrumbTextToFolder(string? text, out string folder)
    {
        folder = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Replace('＞', '>').Replace('›', '>').Trim();
        if (TryNormalizeToExistingDirectory(text.Replace(" > ", "\\"), out folder))
            return true;

        if (!text.Contains('>'))
            return false;

        var parts = text.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        string? volume = null;
        foreach (var partRaw in parts)
        {
            var part = partRaw.Trim();
            if (part.Length == 0) continue;

            if (part.Contains("此电脑", StringComparison.Ordinal)
                || part.Equals("This PC", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Computer", StringComparison.OrdinalIgnoreCase))
            {
                volume = "";
                continue;
            }

            if (TryDriveRootFromBreadcrumbSegment(part, out var driveRoot))
            {
                volume = driveRoot;
                continue;
            }

            if (WpsKnownFolderToPath(part) is { } known)
            {
                volume = known;
                continue;
            }

            if (volume == null)
                continue;
            // 「此电脑」之后 volume 为空串：尚无盘符/根路径时不应拼相对路径。
            if (volume.Length == 0)
                continue;

            try
            {
                var next = Path.Combine(volume, part);
                volume = Path.GetFullPath(next);
            }
            catch
            {
                return false;
            }
        }

        if (string.IsNullOrEmpty(volume)) return false;
        try
        {
            if (!Directory.Exists(volume)) return false;
            folder = Path.GetFullPath(volume);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDriveRootFromBreadcrumbSegment(string segment, out string driveRoot)
    {
        driveRoot = "";
        if (TryNormalizeToExistingDirectory(segment, out var direct))
        {
            try
            {
                if (Directory.Exists(direct))
                {
                    driveRoot = Path.GetFullPath(direct);
                    return true;
                }
            }
            catch
            {
                /* ignore */
            }
        }

        var i = segment.LastIndexOf('(');
        if (i >= 0)
        {
            var j = segment.IndexOf(')', i);
            if (j > i)
            {
                var inner = segment.AsSpan(i + 1, j - i - 1).Trim();
                if (inner.Length >= 2 && inner[^1] == ':' && char.IsLetter(inner[0]))
                {
                    driveRoot = char.ToUpperInvariant(inner[0]) + @":\";
                    return true;
                }
                if (inner.Length == 1 && char.IsLetter(inner[0]))
                {
                    driveRoot = char.ToUpperInvariant(inner[0]) + @":\";
                    return true;
                }
            }
        }

        var m = Regex.Match(segment, @"^([A-Za-z]):$");
        if (m.Success)
        {
            driveRoot = char.ToUpperInvariant(m.Groups[1].Value[0]) + @":\";
            return true;
        }

        return false;
    }

    private static string? WpsKnownFolderToPath(string display)
    {
        try
        {
            if (display.Contains("桌面", StringComparison.Ordinal))
                return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (display.Contains("文档", StringComparison.Ordinal))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (display.Contains("下载", StringComparison.Ordinal))
            {
                var down = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                return Directory.Exists(down) ? down : null;
            }
            if (display.Contains("图片", StringComparison.Ordinal))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (display.Contains("音乐", StringComparison.Ordinal))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (display.Contains("视频", StringComparison.Ordinal))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            if (display.Equals("Desktop", StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (display.Equals("Documents", StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (display.Equals("Downloads", StringComparison.OrdinalIgnoreCase))
            {
                var down = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                return Directory.Exists(down) ? down : null;
            }
        }
        catch
        {
            /* ignore */
        }

        return null;
    }

    /// <summary>供路径采集等模块复用：从 UI 文本还原为已存在的目录路径。</summary>
    internal static bool TryNormalizeToExistingDirectory(string? raw, out string norm)
    {
        norm = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var v = raw.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1].Trim();

        try
        {
            if (Directory.Exists(v))
            {
                norm = Path.GetFullPath(v);
                return true;
            }

            if (Path.IsPathRooted(v) || v.Contains('\\') || v.Contains('/'))
            {
                var dir = Path.GetDirectoryName(v);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    norm = Path.GetFullPath(dir);
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// “保存的目标”等实际目录：将联接点/符号链接解析为物理路径，减轻 BrowseObject 与地址栏在 Shell/云目录上的失败率。
    /// </summary>
    private static string NormalizeFolderPathForNavigation(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return folderPath;
        try
        {
            var full = Path.GetFullPath(folderPath.Trim());
            if (!Directory.Exists(full)) return full;
            var di = new DirectoryInfo(full);
            DirectoryInfo? concrete = null;
            try
            {
                if (di.LinkTarget != null || (di.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var resolved = di.ResolveLinkTarget(returnFinalTarget: true);
                    if (resolved is DirectoryInfo d && d.Exists)
                        concrete = d;
                }
            }
            catch { /* 非链接或无权解析则沿用 full */ }

            if (concrete != null && concrete.Exists)
                return concrete.FullName.TrimEnd('\\', '/');
            return full.TrimEnd('\\', '/');
        }
        catch
        {
            return folderPath;
        }
    }

    public static bool TryNavigateToFolder(IntPtr dialogHwnd, string folderPath)
    {
        var kind = ClassifyFileDialog(dialogHwnd);
        if (kind == FileDialogKind.None) return false;

        var path = NormalizeFolderPathForNavigation(folderPath);
        if (!Directory.Exists(path)) return false;

        if (kind != FileDialogKind.WpsCustom
            && ShellDialogDeepNavigate.TryBrowseObjectInject(dialogHwnd, path))
            return true;

        if (kind == FileDialogKind.SysListView)
            return TryNavigateSysListViewStyle(dialogHwnd, path);
        if (kind == FileDialogKind.WpsCustom)
            return TryNavigateWpsCustom(dialogHwnd, path);
        return TryNavigateAddressBarStyle(dialogHwnd, path);
    }

    /// <summary>WPS：无 IShellBrowser，优先 UIA 可写路径；否则用底部「文件名」Edit 填目录+回车（常见与系统对话框类似）。</summary>
    private static bool TryNavigateWpsCustom(IntPtr dialogHwnd, string folderPath)
    {
        if (!Directory.Exists(folderPath)) return false;
        var norm = Path.GetFullPath(folderPath);
        var folderWithSlash = norm.TrimEnd('\\', '/') + "\\";

        uint dialogPid;
        var dialogTid = Win32.GetWindowThreadProcessId(dialogHwnd, out dialogPid);
        var curTid = Win32.GetCurrentThreadId();
        Win32.AttachThreadInput(curTid, dialogTid, true);
        try { Win32.SetForegroundWindow(dialogHwnd); }
        finally { Win32.AttachThreadInput(curTid, dialogTid, false); }

        Thread.Sleep(100);

        if (TryWpsSetPathViaValuePattern(dialogHwnd, norm))
        {
            Thread.Sleep(60);
            SendEnter();
            Thread.Sleep(120);
            return true;
        }

        var bottomEdit = FindBottomMostEditHwnd(dialogHwnd);
        if (bottomEdit != IntPtr.Zero)
        {
            var cap = (int)(long)Win32.SendMessage(bottomEdit, Win32.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
            if (cap < 0) cap = 0;
            cap = Math.Min(cap + 64, 32767);
            var oldSb = new StringBuilder(Math.Max(cap, 256));
            Win32.SendMessage(bottomEdit, Win32.WM_GETTEXT, (IntPtr)oldSb.Capacity, oldSb);
            var oldText = oldSb.ToString();

            Win32.SendMessage(bottomEdit, Win32.WM_SETTEXT, IntPtr.Zero, folderWithSlash);
            Win32.SetFocus(bottomEdit);
            Thread.Sleep(40);
            SendEnter();
            Thread.Sleep(120);

            Win32.SendMessage(bottomEdit, Win32.WM_SETTEXT, IntPtr.Zero, oldText);
            return true;
        }

        SendCtrlL();
        Thread.Sleep(120);
        SendUnicodeString(norm);
        Thread.Sleep(50);
        SendEnter();
        return true;
    }

    private static bool TryWpsSetPathViaValuePattern(IntPtr dialogHwnd, string fullPath)
    {
        try
        {
            var root = AutomationElement.FromHandle(dialogHwnd);
            if (root == null) return false;
            AutomationElement? bestEl = null;
            var bestScore = -1;
            var q = new Queue<AutomationElement>();
            q.Enqueue(root);
            for (var seen = 0; q.Count > 0 && seen < 400; seen++)
            {
                var el = q.Dequeue();
                try
                {
                    foreach (AutomationElement c in el.FindAll(TreeScope.Children, Condition.TrueCondition))
                        q.Enqueue(c);
                }
                catch { /* ignore */ }

                try
                {
                    if (!el.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj))
                        continue;
                    if (!((ValuePattern)vpObj).Current.IsReadOnly)
                    {
                        var score = 1;
                        try
                        {
                            var n = el.Current.Name ?? "";
                            if (n.Contains("地址", StringComparison.Ordinal)
                                || n.Contains("路径", StringComparison.Ordinal)
                                || n.Contains("位置", StringComparison.Ordinal))
                                score += 4;
                            if (n.Contains("文件", StringComparison.Ordinal) && n.Contains("名", StringComparison.Ordinal))
                                score += 2;
                        }
                        catch { /* ignore */ }

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestEl = el;
                        }
                    }
                }
                catch { /* ignore */ }
            }

            if (bestEl == null) return false;
            if (!bestEl.TryGetCurrentPattern(ValuePattern.Pattern, out var useVp)) return false;
            ((ValuePattern)useVp).SetValue(fullPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>取屏幕上最靠下的 Win32 Edit，通常为「文件名(N)」编辑框，可用于路径跳转。</summary>
    private static IntPtr FindBottomMostEditHwnd(IntPtr root)
    {
        IntPtr best = IntPtr.Zero;
        var bestBottom = int.MinValue;
        void Walk(IntPtr h)
        {
            if (string.Equals(Win32.GetWindowClassName(h), "Edit", StringComparison.Ordinal))
            {
                if (Win32.GetWindowRect(h, out var r) && r.Bottom >= bestBottom)
                {
                    bestBottom = r.Bottom;
                    best = h;
                }
            }
            Win32.EnumChildWindows(h, (c, _) =>
            {
                Walk(c);
                return true;
            }, IntPtr.Zero);
        }
        Walk(root);
        return best;
    }

    /// <summary>QuickSwitch FeedDialogSYSLISTVIEW：改 Edit1 为文件夹路径并回车，再恢复原文件名。</summary>
    private static bool TryNavigateSysListViewStyle(IntPtr dialogHwnd, string folderPath)
    {
        if (!Directory.Exists(folderPath)) return false;
        var norm = Path.GetFullPath(folderPath);
        var folderWithSlash = norm.TrimEnd('\\') + "\\";

        var edit1 = FindFirstEditControl(dialogHwnd);
        if (edit1 == IntPtr.Zero) return false;

        uint dialogPid;
        var dialogTid = Win32.GetWindowThreadProcessId(dialogHwnd, out dialogPid);
        var curTid = Win32.GetCurrentThreadId();
        Win32.AttachThreadInput(curTid, dialogTid, true);
        try { Win32.SetForegroundWindow(dialogHwnd); }
        finally { Win32.AttachThreadInput(curTid, dialogTid, false); }

        Thread.Sleep(40);

        var cap = (int)(long)Win32.SendMessage(edit1, Win32.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
        if (cap < 0) cap = 0;
        cap = Math.Min(cap + 64, 32767);
        var oldSb = new StringBuilder(Math.Max(cap, 256));
        Win32.SendMessage(edit1, Win32.WM_GETTEXT, (IntPtr)oldSb.Capacity, oldSb);
        var oldText = oldSb.ToString();

        Win32.SendMessage(edit1, Win32.WM_SETTEXT, IntPtr.Zero, folderWithSlash);
        Win32.SetFocus(edit1);
        Thread.Sleep(30);
        SendEnter();
        Thread.Sleep(80);

        Win32.SendMessage(edit1, Win32.WM_SETTEXT, IntPtr.Zero, oldText);
        return true;
    }

    private static IntPtr FindFirstEditControl(IntPtr root)
    {
        IntPtr found = IntPtr.Zero;
        void Walk(IntPtr h)
        {
            if (found != IntPtr.Zero) return;
            if (string.Equals(Win32.GetWindowClassName(h), "Edit", StringComparison.Ordinal))
            {
                found = h;
                return;
            }
            Win32.EnumChildWindows(h, (c, _) =>
            {
                Walk(c);
                return true;
            }, IntPtr.Zero);
        }
        Walk(root);
        return found;
    }

    private static bool TryNavigateAddressBarStyle(IntPtr dialogHwnd, string folderPath)
    {
        if (!Directory.Exists(folderPath)) return false;
        var norm = Path.GetFullPath(folderPath);

        uint dialogPid;
        var dialogTid = Win32.GetWindowThreadProcessId(dialogHwnd, out dialogPid);
        var curTid = Win32.GetCurrentThreadId();

        Win32.AttachThreadInput(curTid, dialogTid, true);
        try { Win32.SetForegroundWindow(dialogHwnd); }
        finally { Win32.AttachThreadInput(curTid, dialogTid, false); }

        Thread.Sleep(60);
        SendCtrlL();
        Thread.Sleep(140);

        if (TrySetFocusedAddressValue(norm))
        {
            Thread.Sleep(40);
            SendEnter();
            return true;
        }

        SendCtrlA();
        Thread.Sleep(30);
        SendUnicodeString(norm);
        Thread.Sleep(30);
        SendEnter();
        return true;
    }

    private static bool TrySetFocusedAddressValue(string path)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null) return false;
            if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out var p))
            {
                ((ValuePattern)p).SetValue(path);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static void SendCtrlL()
    {
        var inputs = new Win32.INPUT[4];
        inputs[0].type = Win32.INPUT_KEYBOARD; inputs[0].u.ki.wVk = Win32.VK_CONTROL;
        inputs[1].type = Win32.INPUT_KEYBOARD; inputs[1].u.ki.wVk = Win32.VK_L;
        inputs[2].type = Win32.INPUT_KEYBOARD; inputs[2].u.ki.wVk = Win32.VK_L; inputs[2].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        inputs[3].type = Win32.INPUT_KEYBOARD; inputs[3].u.ki.wVk = Win32.VK_CONTROL; inputs[3].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        Win32.SendInput(4, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    private static void SendCtrlA()
    {
        var inputs = new Win32.INPUT[4];
        inputs[0].type = Win32.INPUT_KEYBOARD; inputs[0].u.ki.wVk = Win32.VK_CONTROL;
        inputs[1].type = Win32.INPUT_KEYBOARD; inputs[1].u.ki.wVk = Win32.VK_A;
        inputs[2].type = Win32.INPUT_KEYBOARD; inputs[2].u.ki.wVk = Win32.VK_A; inputs[2].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        inputs[3].type = Win32.INPUT_KEYBOARD; inputs[3].u.ki.wVk = Win32.VK_CONTROL; inputs[3].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        Win32.SendInput(4, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    private static void SendEnter()
    {
        var inputs = new Win32.INPUT[2];
        inputs[0].type = Win32.INPUT_KEYBOARD; inputs[0].u.ki.wVk = (ushort)Win32.VK_RETURN;
        inputs[1].type = Win32.INPUT_KEYBOARD; inputs[1].u.ki.wVk = (ushort)Win32.VK_RETURN; inputs[1].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        Win32.SendInput(2, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    private static void SendUnicodeString(string s)
    {
        var list = new List<Win32.INPUT>(s.Length * 2);
        foreach (var c in s)
        {
            var u = (ushort)c;
            list.Add(new Win32.INPUT
            {
                type = Win32.INPUT_KEYBOARD,
                u = new Win32.INPUTUNION
                {
                    ki = new Win32.KEYBDINPUT
                    {
                        wVk = 0, wScan = u,
                        dwFlags = Win32.KEYEVENTF_UNICODE, time = 0, dwExtraInfo = IntPtr.Zero
                    }
                }
            });
            list.Add(new Win32.INPUT
            {
                type = Win32.INPUT_KEYBOARD,
                u = new Win32.INPUTUNION
                {
                    ki = new Win32.KEYBDINPUT
                    {
                        wVk = 0, wScan = u,
                        dwFlags = Win32.KEYEVENTF_UNICODE | Win32.KEYEVENTF_KEYUP,
                        time = 0, dwExtraInfo = IntPtr.Zero
                    }
                }
            });
        }
        var arr = list.ToArray();
        Win32.SendInput((uint)arr.Length, arr, Marshal.SizeOf<Win32.INPUT>());
    }
}
