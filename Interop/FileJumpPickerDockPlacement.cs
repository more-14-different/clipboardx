using System.Runtime.InteropServices;

namespace ClipboardManager;

/// <summary>跳转列表紧贴文件对话框侧边（默认右侧），按物理像素计算左上角。</summary>
internal static class FileJumpPickerDockPlacement
{
    public static bool TryComputePosition(nint dialogHwnd, int popupW, int popupH, out int x, out int y)
    {
        x = y = 0;
        if (dialogHwnd == 0 || !Win32.GetWindowRect(dialogHwnd, out var dr))
            return false;

        var center = new Win32.POINT { X = (dr.Left + dr.Right) / 2, Y = (dr.Top + dr.Bottom) / 2 };
        var mon = Win32.MonitorFromPoint(center, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(mon, ref mi))
            mi.rcWork = new Win32.RECT { Left = 0, Top = 0, Right = 65535, Bottom = 65535 };

        x = dr.Right;
        y = dr.Top;

        if (x + popupW > mi.rcWork.Right)
            x = dr.Left - popupW;
        if (x < mi.rcWork.Left)
            x = mi.rcWork.Left;
        if (x + popupW > mi.rcWork.Right)
            x = Math.Max(mi.rcWork.Left, mi.rcWork.Right - popupW);

        if (y < mi.rcWork.Top)
            y = mi.rcWork.Top;
        if (y + popupH > mi.rcWork.Bottom)
            y = mi.rcWork.Bottom - popupH;

        return true;
    }
}
