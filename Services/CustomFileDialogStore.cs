using System.IO;
using System.Text;
using System.Text.Json;

namespace ClipboardManager;

/// <summary>
/// 持久化自定义文件对话框规则（与 settings.json 同目录下的独立文件）。
/// </summary>
internal static class CustomFileDialogStore
{
    /// <summary>成功写入磁盘后触发，供设置界面等刷新列表。</summary>
    internal static event Action? RulesChanged;
    /// <summary>策略 id，与 <see cref="FileDialogJumpHelper"/> 内分支一致。</summary>
    public static readonly string[] DefaultStrategyOrder =
    {
        "shell_inject",
        "sys_listview",
        "address_bar",
        "wps_chain",
        "qt_alt_n",
        "alt_d_value_enter",
        "ctrl_l_type_enter",
    };

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly string StoreFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClipboardX",
        "custom_file_dialogs.json");

    private static List<CustomFileDialogRule>? _rules;
    private static readonly object Sync = new();

    /// <summary>当前规则文件完整路径（与 AppData ClipboardX 下 settings.json 同目录）。</summary>
    internal static string PersistencePath => StoreFile;

    private sealed class Doc
    {
        public List<CustomFileDialogRule> Rules { get; set; } = new();
    }

    public static IReadOnlyList<CustomFileDialogRule> GetRules()
    {
        lock (Sync) return EnsureLoaded().ToList();
    }

    public static CustomFileDialogRule? FindMatchingRule(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd))
            return null;

        var className = Win32.GetWindowClassName(hwnd);
        var title = Win32.GetWindowText(hwnd);
        Win32.GetWindowThreadProcessId(hwnd, out var pid);

        var exeBase = "";
        if (pid != 0)
        {
            var hProc = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    if (Win32.GetModuleFileNameEx(hProc, IntPtr.Zero, sb, sb.Capacity) > 0)
                        exeBase = Path.GetFileNameWithoutExtension(sb.ToString()).ToLowerInvariant();
                }
                finally { Win32.CloseHandle(hProc); }
            }
        }

        lock (Sync)
        {
            foreach (var r in EnsureLoaded())
            {
                if (!string.Equals(r.WindowClass, className, StringComparison.Ordinal))
                    continue;
                if (!string.IsNullOrEmpty(r.ProcessExeBase)
                    && !string.Equals(r.ProcessExeBase, exeBase, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(r.TitleContains)
                    && (title == null || title.IndexOf(r.TitleContains, StringComparison.Ordinal) < 0))
                    continue;
                return r;
            }
        }

        return null;
    }

    public static void UpsertRule(CustomFileDialogRule rule)
    {
        lock (Sync)
        {
            var list = EnsureLoaded();
            list.RemoveAll(x => RuleKey(x) == RuleKey(rule));
            list.Insert(0, rule);
            PersistUnlocked(list);
        }
    }

    public static void RemoveRule(string id)
    {
        lock (Sync)
        {
            var list = EnsureLoaded();
            if (list.RemoveAll(x => x.Id == id) > 0)
                PersistUnlocked(list);
        }
    }

    /// <summary>导出为与内置存储相同结构的 JSON 文件。</summary>
    internal static void ExportToFile(string path)
    {
        List<CustomFileDialogRule> snapshot;
        lock (Sync)
            snapshot = EnsureLoaded().Select(CloneRule).ToList();
        var json = JsonSerializer.Serialize(new Doc { Rules = snapshot }, JsonOpt);
        File.WriteAllText(path, json);
    }

    /// <summary>导入并与本地合并：相同「类名+进程+标题包含」键的规则被覆盖。</summary>
    internal static int ImportMergeFromFile(string path, out string? error)
    {
        error = null;
        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<Doc>(json, JsonOpt);
            if (doc?.Rules == null)
            {
                error = "JSON 中缺少 rules 数组";
                return 0;
            }

            lock (Sync)
            {
                var list = EnsureLoaded().ToList();
                var applied = 0;
                foreach (var r in doc.Rules)
                {
                    var n = NormalizeImportedRule(r);
                    if (string.IsNullOrEmpty(n.WindowClass))
                        continue;
                    list.RemoveAll(x => RuleKey(x) == RuleKey(n));
                    list.Insert(0, n);
                    applied++;
                }

                _rules = list;
                PersistUnlocked(list);
                return applied;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return 0;
        }
    }

    /// <summary>导入并完全替换当前规则列表。</summary>
    internal static int ImportReplaceFromFile(string path, out string? error)
    {
        error = null;
        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<Doc>(json, JsonOpt);
            if (doc?.Rules == null)
            {
                error = "JSON 中缺少 rules 数组";
                return 0;
            }

            lock (Sync)
            {
                _rules = doc.Rules
                    .Select(NormalizeImportedRule)
                    .Where(x => !string.IsNullOrEmpty(x.WindowClass))
                    .ToList();
                PersistUnlocked(_rules);
                return _rules.Count;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return 0;
        }
    }

    private static CustomFileDialogRule CloneRule(CustomFileDialogRule r) => new()
    {
        Id = r.Id,
        Note = r.Note,
        ProcessExeBase = r.ProcessExeBase,
        WindowClass = r.WindowClass,
        TitleContains = r.TitleContains,
        StrategyOrder = r.StrategyOrder != null ? [.. r.StrategyOrder] : new List<string>(),
        PinnedStrategy = r.PinnedStrategy,
    };

    private static CustomFileDialogRule NormalizeImportedRule(CustomFileDialogRule r)
    {
        var id = string.IsNullOrWhiteSpace(r.Id) ? Guid.NewGuid().ToString("N") : r.Id.Trim();
        return new CustomFileDialogRule
        {
            Id = id,
            Note = r.Note ?? "",
            ProcessExeBase = r.ProcessExeBase?.Trim() ?? "",
            WindowClass = r.WindowClass?.Trim() ?? "",
            TitleContains = r.TitleContains?.Trim() ?? "",
            StrategyOrder = r.StrategyOrder is { Count: > 0 }
                ? r.StrategyOrder.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList()
                : new List<string>(),
            PinnedStrategy = string.IsNullOrWhiteSpace(r.PinnedStrategy) ? null : r.PinnedStrategy.Trim(),
        };
    }

    private static string RuleKey(CustomFileDialogRule r) =>
        $"{r.WindowClass}\u001f{r.ProcessExeBase}\u001f{r.TitleContains}";

    private static List<CustomFileDialogRule> EnsureLoaded()
    {
        if (_rules != null)
            return _rules;
        try
        {
            if (File.Exists(StoreFile))
            {
                var json = File.ReadAllText(StoreFile);
                var doc = JsonSerializer.Deserialize<Doc>(json, JsonOpt);
                _rules = doc?.Rules ?? new List<CustomFileDialogRule>();
            }
            else
            {
                _rules = new List<CustomFileDialogRule>();
            }
        }
        catch
        {
            _rules = new List<CustomFileDialogRule>();
        }

        return _rules;
    }

    private static void PersistUnlocked(List<CustomFileDialogRule> list)
    {
        try
        {
            var dir = Path.GetDirectoryName(StoreFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new Doc { Rules = list }, JsonOpt);
            File.WriteAllText(StoreFile, json);
            RulesChanged?.Invoke();
        }
        catch { /* ignore */ }
    }
}
