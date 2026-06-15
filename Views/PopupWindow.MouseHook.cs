using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace ClipboardManager;

public partial class PopupWindow : Window
{
    private static IntPtr StaticMouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var owner = s_popupMouseHookOwner;
        var hhk = s_popupMouseHookForNext;
        if (owner != null && hhk != IntPtr.Zero)
            return owner.MouseHookCallback(nCode, wParam, lParam);
        return Win32.CallNextHookEx(hhk, nCode, wParam, lParam);
    }

    private static IntPtr StaticFileJumpAutoMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var owner = s_fileJumpAutoMouseOwner;
        var hhk = s_fileJumpAutoMouseHookForNext;
        if (owner != null && hhk != IntPtr.Zero)
            return owner.FileJumpAutoMouseHookCallback(nCode, wParam, lParam);
        return Win32.CallNextHookEx(hhk, nCode, wParam, lParam);
    }

#if CLIPX_FILEJUMP
    private static IntPtr StaticFileJumpPersistMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var owner = s_fileJumpPersistMouseOwner;
        var hhk = s_fileJumpPersistMouseHookForNext;
        if (owner != null && hhk != IntPtr.Zero)
            return owner.FileJumpPersistMouseHookCallback(nCode, wParam, lParam);
        return Win32.CallNextHookEx(hhk, nCode, wParam, lParam);
    }
#endif

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        s_popupMouseHookOwner = this;
        ClipboardManager.Services.GlobalHookDispatcher.Dispatcher.Invoke(() =>
        {
            _mouseHook = Win32.SetWindowsHookEx(
                Win32.WH_MOUSE_LL, s_popupMouseHookThunk, Win32.GetModuleHandle(null), 0);
            s_popupMouseHookForNext = _mouseHook;
        });
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            var hk = _mouseHook;
            _mouseHook = IntPtr.Zero;
            ClipboardManager.Services.GlobalHookDispatcher.Dispatcher.Invoke(() => Win32.UnhookWindowsHookEx(hk));
        }
        if (s_popupMouseHookOwner == this)
        {
            s_popupMouseHookOwner = null;
            s_popupMouseHookForNext = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isPopupVisible)
        {
            var msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);

            if (_isDragging)
            {
                if (msg == Win32.WM_LBUTTONUP)
                {
                    // 松手时刷新节流期间累积的最后一帧位置
                    if (_hasPendingDragMove)
                    {
                        _hasPendingDragMove = false;
                        _isOurSetWindowPos = true;
                        try
                        {
                            Win32.SetWindowPos(_hwnd, IntPtr.Zero,
                                _pendingDragX, _pendingDragY, 0, 0,
                                Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE
                                | Win32.SWP_NOSENDCHANGING);
                        }
                        finally { _isOurSetWindowPos = false; }
                        Win32.GetWindowRect(_hwnd, out var rcPending);
                        _hookAuthPhysLeft = rcPending.Left;
                        _hookAuthPhysTop = rcPending.Top;
                    }

                    // 壳/DWM 可能在钩子最后一帧 WM_MOUSEMOVE 与松手之间改写 HWND；此处立即对齐权威位置，减少与 BeginInvoke(Sync) 的竞态。
                    if (Win32.GetWindowRect(_hwnd, out var rcUp))
                    {
                        int dl = Math.Abs(rcUp.Left - _hookAuthPhysLeft);
                        int dt = Math.Abs(rcUp.Top - _hookAuthPhysTop);
                        if (dl > 8 || dt > 8)
                        {
                            #region agent log
                            AgentDbgLog("H15", "MouseHook WM_LBUTTONUP", "immediate drift vs hook auth; restoring",
                                new { rcUp.Left, rcUp.Top, _hookAuthPhysLeft, _hookAuthPhysTop, dl, dt });
                            #endregion
                            _isOurSetWindowPos = true;
                            try
                            {
                                Win32.SetWindowPos(_hwnd, IntPtr.Zero, _hookAuthPhysLeft, _hookAuthPhysTop, 0, 0,
                                    Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOSENDCHANGING);
                            }
                            finally
                            {
                                _isOurSetWindowPos = false;
                            }
                            _postDragHookAuthLeft = int.MinValue;
                        }
                        else
                        {
                            _postDragHookAuthLeft = _hookAuthPhysLeft;
                            _postDragHookAuthTop = _hookAuthPhysTop;
                        }
                    }
                    else
                    {
                        _postDragHookAuthLeft = _hookAuthPhysLeft;
                        _postDragHookAuthTop = _hookAuthPhysTop;
                    }

                    _isDragging = false;
                    #region agent log
                    _agentDbgDragMoveLogCount = 0;
                    #endregion
                    // 拖动结束再同步 WPF Left/Top；拖动过程中若每帧 TransformFromDevice，跨屏时与 HWND 实际所在监视器 DPI 不一致会导致错位。
                    Dispatcher.BeginInvoke(DispatcherPriority.Input,
                        new Action(() => SyncWindowPhysicalPositionToWpf("mouseHookLButtonUp")));
                }
                else if (msg == Win32.WM_MOUSEMOVE)
                {
                    // 必须用 GetCursorPos 与 Header_DragStart 一致：运行时日志显示 MSLLHOOKSTRUCT.pt 与 GetCursorPos
                    // 在混合 DPI 下可差 2 倍（如 pt.X=3221 而 GetCursorPos=6442），用 pt 算 dx 会把窗口 SetWindowPos 到错误屏区。
                    if (!Win32.GetCursorPos(out var curPt))
                        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                    var dx = curPt.X - _dragLastPt.X;
                    var dy = curPt.Y - _dragLastPt.Y;
                    if (dx != 0 || dy != 0)
                    {
                        if (Win32.GetWindowRect(_hwnd, out var rc))
                        {
                            var nx = rc.Left + dx;
                            var ny = rc.Top + dy;

                            // 节流：限制 SetWindowPos 频率到 ~120fps（8ms 间隔），减少 DWM/Shell 争用
                            var nowTick = Environment.TickCount64;
                            if (nowTick - _lastDragMoveTick < 8)
                            {
                                _pendingDragX = nx;
                                _pendingDragY = ny;
                                _hasPendingDragMove = true;
                                _dragLastPt = curPt;
                                return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                            }
                            _lastDragMoveTick = nowTick;
                            _hasPendingDragMove = false;

                            _isOurSetWindowPos = true;
                            try
                            {
                                Win32.SetWindowPos(_hwnd, IntPtr.Zero,
                                    nx, ny, 0, 0,
                                    Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE
                                    | Win32.SWP_NOSENDCHANGING);
                            }
                            finally
                            {
                                _isOurSetWindowPos = false;
                            }

                            Win32.GetWindowRect(_hwnd, out var rcAfter);
                            _hookAuthPhysLeft = rcAfter.Left;
                            _hookAuthPhysTop = rcAfter.Top;

                            // 拖动中不同步 WPF Left/Top：日志显示每帧 Sync 与 SetWindowPos 竞争，且 desk→TryApply 曾把物理像素当 DIP，导致 HWND 跳到 2× 位置（如 486→972）。
                            #region agent log
                            if (_agentDbgH21MismatchLogCount < 50 &&
                                (Math.Abs(rcAfter.Left - nx) > 16 || Math.Abs(rcAfter.Top - ny) > 16))
                            {
                                _agentDbgH21MismatchLogCount++;
                                AgentDbgLog("H21", "MouseHook WM_MOUSEMOVE", "SetWindowPos vs GetWindowRect mismatch (shell/DWM?)",
                                    new
                                    {
                                        nx,
                                        ny,
                                        actualLeft = rcAfter.Left,
                                        actualTop = rcAfter.Top,
                                        dlx = rcAfter.Left - nx,
                                        dty = rcAfter.Top - ny
                                    });
                            }
                            #endregion
                            #region agent log
                            if (_agentDbgDragMoveLogCount < 500)
                            {
                                _agentDbgDragMoveLogCount++;
                                AgentDbgLog("H3", "MouseHookCallback WM_MOUSEMOVE", "after SetWindowPos+Sync",
                                    new
                                    {
                                        nx,
                                        ny,
                                        rcAfter.Left,
                                        rcAfter.Top,
                                        wpfLeft = Left,
                                        wpfTop = Top,
                                        cursorX = curPt.X,
                                        cursorY = curPt.Y,
                                        hookPtX = info.pt.X,
                                        hookPtY = info.pt.Y
                                    });
                            }
                            #endregion
                            #region agent log
                            if (_agentDbgH20LogCount < 40 && _agentDbgCachedPrimarySeamX != int.MinValue)
                            {
                                int sx = _agentDbgCachedPrimarySeamX;
                                if (rcAfter.Left < sx && rcAfter.Right > sx)
                                {
                                    int ww = rcAfter.Right - rcAfter.Left;
                                    if (ww > 0)
                                    {
                                        double frac = (sx - rcAfter.Left) / (double)ww;
                                        AgentDbgLog("H20", "MouseHook WM_MOUSEMOVE", "straddle primary vertical seam",
                                            new { sx, rcAfter.Left, rcAfter.Right, fracFromWindowLeft = frac });
                                        _agentDbgH20LogCount++;
                                    }
                                }
                            }
                            #endregion
                            #region agent log
                            if (_agentDbgH22WpfHwndDipLogCount < 30 &&
                                _agentDbgCachedPrimarySeamX != int.MinValue)
                            {
                                int sx = _agentDbgCachedPrimarySeamX;
                                if (rcAfter.Left < sx && rcAfter.Right > sx)
                                {
                                    int wStr = rcAfter.Right - rcAfter.Left;
                                    int hStr = rcAfter.Bottom - rcAfter.Top;
                                    if (wStr > 0 && hStr > 0 &&
                                        TryPhysicalScreenTopLeftToWpfDip(rcAfter.Left, rcAfter.Top, wStr, hStr, out var expL, out var expT, agentLogH19: false))
                                    {
                                        double dL = Left - expL;
                                        double dT = Top - expT;
                                        if (Math.Abs(dL) > 3 || Math.Abs(dT) > 3)
                                        {
                                            _agentDbgH22WpfHwndDipLogCount++;
                                            AgentDbgLog("H22", "MouseHook WM_MOUSEMOVE", "WPF Left/Top vs hwnd→DIP (straddle)",
                                                new { wpfLeft = Left, wpfTop = Top, expL, expT, dL, dT, physLeft = rcAfter.Left, physTop = rcAfter.Top });
                                        }
                                    }
                                }
                            }
                            #endregion
                        }

                        _dragLastPt = curPt;
                    }
                }

                return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            if (msg is Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN)
            {
                if (_hideOnSameAppClick)
                {
                    _clickReceivedByPopup = false;
                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background, () =>
                        {
                            if (_isPopupVisible && !_clickReceivedByPopup
                                && !_isContextPopupOpen && !_isPhraseEditPopupOpen && !_isTextEntryEditPopupOpen)
                                HidePopup();
                        });
                }
            }
        }
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void InstallFileJumpAutoMouseHook()
    {
        if (_fileJumpAutoMouseHook != IntPtr.Zero) return;
        s_fileJumpAutoMouseOwner = this;
        ClipboardManager.Services.GlobalHookDispatcher.Dispatcher.Invoke(() =>
        {
            _fileJumpAutoMouseHook = Win32.SetWindowsHookEx(
                Win32.WH_MOUSE_LL, s_fileJumpAutoMouseThunk, Win32.GetModuleHandle(null), 0);
            s_fileJumpAutoMouseHookForNext = _fileJumpAutoMouseHook;
        });
    }

#if CLIPX_FILEJUMP
    private void InstallFileJumpPersistFolderHook()
    {
        if (_fileJumpPersistMouseHook != IntPtr.Zero) return;
        s_fileJumpPersistMouseOwner = this;
        ClipboardManager.Services.GlobalHookDispatcher.Dispatcher.Invoke(() =>
        {
            _fileJumpPersistMouseHook = Win32.SetWindowsHookEx(
                Win32.WH_MOUSE_LL, s_fileJumpPersistMouseThunk, Win32.GetModuleHandle(null), 0);
            s_fileJumpPersistMouseHookForNext = _fileJumpPersistMouseHook;
        });
    }

    private void UninstallFileJumpPersistFolderHook()
    {
        if (_fileJumpPersistMouseHook != IntPtr.Zero)
        {
            var hk = _fileJumpPersistMouseHook;
            _fileJumpPersistMouseHook = IntPtr.Zero;
            ClipboardManager.Services.GlobalHookDispatcher.Dispatcher.Invoke(() => Win32.UnhookWindowsHookEx(hk));
        }

        if (s_fileJumpPersistMouseOwner == this)
        {
            s_fileJumpPersistMouseOwner = null;
            s_fileJumpPersistMouseHookForNext = IntPtr.Zero;
        }
    }

    private IntPtr FileJumpPersistMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0
            && _appSettings != null
            && wParam.ToInt32() == Win32.WM_LBUTTONDOWN)
        {
            var sinceLastDialog = Environment.TickCount64 - _lastFileDialogSeenTick;
            if (sinceLastDialog < FileDialogAliveWindowMs && sinceLastDialog >= 0)
            {
                var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                if (FileDialogConfirmClick.TryResolveDialogOnPrimaryConfirmClick(info.pt, out var dialogHwnd))
                {
                    var dlgCapture = dialogHwnd;
                    Dispatcher.BeginInvoke(new Action(() => TryPersistRecentFolderAfterPrimaryClick(dlgCapture)),
                        DispatcherPriority.Send);
                }
            }
        }

        return Win32.CallNextHookEx(_fileJumpPersistMouseHook, nCode, wParam, lParam);
    }

    private void TryPersistRecentFolderAfterPrimaryClick(IntPtr dialogHwnd)
    {
        try
        {
            if (_appSettings == null) return;
            if (dialogHwnd == IntPtr.Zero || !Win32.IsWindow(dialogHwnd)) return;
            // DLL 注入读取路径较慢，放到后台线程避免阻塞 UI
            var capturedDlg = dialogHwnd;
            var th = new Thread(() =>
            {
                if (!FileDialogJumpHelper.TryReadCurrentFolder(capturedDlg, out var folder)
                    || string.IsNullOrEmpty(folder)) return;
                Dispatcher.BeginInvoke(() => RememberLastDialogFolder(folder), DispatcherPriority.Background);
            }) { IsBackground = true, Name = "ClipboardX-PersistFolder" };
            th.Start();
        }
        catch (Exception ex)
        {
            ShellNavigateLog.Write("filejump", "TryPersistRecentFolderAfterPrimaryClick: " + ex);
        }
    }
#endif

    private IntPtr FileJumpAutoMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0
            && _fileJumpAutoArmedRoot != IntPtr.Zero
            && _appSettings != null
            && _appSettings.FileJumpAutoOnFirstClick
            && wParam.ToInt32() == Win32.WM_LBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            var clickHwnd = Win32.WindowFromPoint(info.pt);
            if (clickHwnd != IntPtr.Zero
                && Win32.GetAncestor(clickHwnd, Win32.GA_ROOT) == _fileJumpAutoArmedRoot)
            {
                Dispatcher.BeginInvoke(new Action(TryFileJumpAutoNavigateAfterClick),
                    System.Windows.Threading.DispatcherPriority.Normal);
            }
        }
        return Win32.CallNextHookEx(_fileJumpAutoMouseHook, nCode, wParam, lParam);
    }
}
