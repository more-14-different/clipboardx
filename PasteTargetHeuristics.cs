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

    private sealed record TargetSnapshot(string ProcessName, string WindowClass, string WindowTitle);

    private sealed record PasteTargetProfile(
        string Mode,
        string Reason,
        IReadOnlySet<string> ProcessNames,
        IReadOnlySet<string> WindowClasses);

    private static readonly PasteTargetProfile TerminalProfile = new(
        PasteSimulationModes.ShiftInsert,
        "terminal",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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
        },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ConsoleWindowClass",
            "CASCADIA_HOSTING_WINDOW_CLASS",
        });

    private static readonly PasteTargetProfile CtrlVPreferredHostProfile = new(
        PasteSimulationModes.CtrlV,
        "ctrlv-preferred-host",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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
        },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Chrome_WidgetWin_1",
            "MozillaWindowClass",
            "CabinetWClass",
            "ExploreWClass",
        });

    private static readonly IReadOnlyList<PasteTargetProfile> DispatchProfiles =
    [
        TerminalProfile,
        CtrlVPreferredHostProfile,
    ];

    /// <summary>
    /// 若用户配置为 Ctrl+V，检测到终端/控制台时可临时改用 Shift+Insert。
    /// </summary>
    public static bool IsLikelyConsoleOrTerminal(IntPtr hwnd)
    {
        return TryCaptureTarget(hwnd, out var snapshot) && MatchesProfile(snapshot, TerminalProfile);
    }

    public static PasteDispatchDecision DecideMode(IntPtr hwnd, string configuredMode)
    {
        var mode = PasteSimulationModes.Normalize(configuredMode);
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd))
            return new PasteDispatchDecision(mode, "default:no-target", "", "", "");

        if (!TryCaptureTarget(hwnd, out var snapshot))
            return new PasteDispatchDecision(mode, "configured-default", "", "", "");

        foreach (var profile in DispatchProfiles)
        {
            if (MatchesProfile(snapshot, profile))
                return new PasteDispatchDecision(profile.Mode, profile.Reason, snapshot.ProcessName, snapshot.WindowClass, snapshot.WindowTitle);
        }

        return new PasteDispatchDecision(mode, "configured-default", snapshot.ProcessName, snapshot.WindowClass, snapshot.WindowTitle);
    }

    private static bool TryCaptureTarget(IntPtr hwnd, out TargetSnapshot snapshot)
    {
        snapshot = new TargetSnapshot("", "", "");
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd))
            return false;

        var windowClass = Win32.GetWindowClassName(hwnd);
        var windowTitle = Win32.GetWindowText(hwnd);
        var processName = TryGetProcessName(hwnd);
        snapshot = new TargetSnapshot(processName, windowClass, windowTitle);
        return true;
    }

    private static bool MatchesProfile(TargetSnapshot snapshot, PasteTargetProfile profile)
    {
        return (snapshot.WindowClass.Length > 0 && profile.WindowClasses.Contains(snapshot.WindowClass)) ||
               (snapshot.ProcessName.Length > 0 && profile.ProcessNames.Contains(snapshot.ProcessName));
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
