using System.Diagnostics;

namespace ClipboardManager;

/// <summary>
/// 判断粘贴目标是否更像「命令行/终端」：此类窗口历史上对 Ctrl+V 支持不一致，
/// Shift+Insert 更贴近系统/Unix 终端习惯。
/// </summary>
internal static class PasteTargetHeuristics
{
    internal readonly record struct PasteDispatchDecision(
        string Mode,
        string Reason,
        string ProcessName,
        string WindowClass,
        string WindowTitle);

    /// <summary>经典 conhost 控制台、Windows Terminal 宿主等。</summary>
    private static readonly HashSet<string> TerminalWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass",
        "CASCADIA_HOSTING_WINDOW_CLASS",
    };

    /// <summary>常见终端相关进程（不含扩展名）。</summary>
    private static readonly HashSet<string> TerminalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd",
        "powershell",
        "pwsh",
        "windowsterminal",
        "wt",
        "openconsole",
        "conhost",
        "mintty",
        "bash",
        "wsl",
        "wslhost",
        "wezterm-gui",
    };

    /// <summary>浏览器 / Chromium / Electron 类宿主，Ctrl+V 往往比 Shift+Insert 更贴近原生行为。</summary>
    private static readonly HashSet<string> CtrlVPreferredProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome",
        "msedge",
        "firefox",
        "brave",
        "opera",
        "vivaldi",
        "iexplore",
        "explorer",
        "electron",
        "code",
        "code-insiders",
        "cursor",
        "slack",
        "discord",
        "notion",
        "teams",
        "obsidian",
    };

    private static readonly HashSet<string> CtrlVPreferredWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chrome_WidgetWin_1",
        "MozillaWindowClass",
        "CabinetWClass",
        "ExploreWClass",
    };

    /// <summary>
    /// 若用户配置为 Ctrl+V，检测到终端/控制台时可临时改用 Shift+Insert。
    /// </summary>
    public static bool IsLikelyConsoleOrTerminal(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd))
            return false;

        var cls = Win32.GetWindowClassName(hwnd);
        if (cls.Length > 0 && TerminalWindowClasses.Contains(cls))
            return true;

        _ = Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
            return false;

        try
        {
            using var p = Process.GetProcessById((int)pid);
            var name = p.ProcessName;
            if (string.IsNullOrEmpty(name))
                return false;
            return TerminalProcessNames.Contains(name);
        }
        catch
        {
            return false;
        }
    }

    public static PasteDispatchDecision DecideMode(IntPtr hwnd, string configuredMode)
    {
        var mode = PasteSimulationModes.Normalize(configuredMode);
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd))
            return new PasteDispatchDecision(mode, "default:no-target", "", "", "");

        var cls = Win32.GetWindowClassName(hwnd);
        var title = Win32.GetWindowText(hwnd);
        var processName = TryGetProcessName(hwnd);

        if ((cls.Length > 0 && TerminalWindowClasses.Contains(cls)) ||
            (processName.Length > 0 && TerminalProcessNames.Contains(processName)))
        {
            return new PasteDispatchDecision(PasteSimulationModes.ShiftInsert, "terminal", processName, cls, title);
        }

        if ((cls.Length > 0 && CtrlVPreferredWindowClasses.Contains(cls)) ||
            (processName.Length > 0 && CtrlVPreferredProcessNames.Contains(processName)))
        {
            return new PasteDispatchDecision(PasteSimulationModes.CtrlV, "ctrlv-preferred-host", processName, cls, title);
        }

        return new PasteDispatchDecision(mode, "configured-default", processName, cls, title);
    }

    private static string TryGetProcessName(IntPtr hwnd)
    {
        _ = Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
            return "";

        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName ?? "";
        }
        catch
        {
            return "";
        }
    }
}
