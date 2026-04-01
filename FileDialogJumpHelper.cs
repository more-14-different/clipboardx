using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
}

internal static class FileDialogJumpHelper
{
    public static FileDialogKind ClassifyFileDialog(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return FileDialogKind.None;
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
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return false;

            var best = "";
            var q = new Queue<AutomationElement>();
            q.Enqueue(root);
            var seen = 0;
            while (q.Count > 0 && seen < 150)
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

    private static bool TryNormalizeToExistingDirectory(string? raw, out string norm)
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

        if (ShellDialogDeepNavigate.TryBrowseObjectInject(dialogHwnd, path))
            return true;

        if (kind == FileDialogKind.SysListView)
            return TryNavigateSysListViewStyle(dialogHwnd, path);
        return TryNavigateAddressBarStyle(dialogHwnd, path);
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
