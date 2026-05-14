using System.Runtime.InteropServices;
using System.Windows;

namespace ClipboardManager;

public partial class PopupWindow : Window
{
    private static bool IsMenuAltVk(uint vk) =>
        vk == 0x12 || vk == 0xA4 || vk == 0xA5;

    /// <summary>批量子菜单/右键菜单打开时：钩子内按设置匹配与 RegisterHotKey 相同的组合（Alt 已由本钩吞掉，宿主收不到）。</summary>
    private bool TryDispatchRegisteredAppHotkeyChordFromHook(uint vkCode)
    {
        if (_appSettings == null) return false;
#if CLIPX_CLIPBOARD
        if (HotkeyChordMatches(_hotkeyModifiers) && vkCode == _hotkeyKey)
        {
            Dispatcher.BeginInvoke(TogglePopup);
            return true;
        }
#endif
        if (HotkeyChordMatches(_appSettings.BatchModeCycleHotkeyModifiers)
            && vkCode == _appSettings.BatchModeCycleHotkeyKey)
        {
            Dispatcher.BeginInvoke(CycleBatchPasteMode);
            return true;
        }
        if (HotkeyChordMatches(_fileJumpHotkeyModifiers) && vkCode == _fileJumpHotkeyKey)
        {
            Dispatcher.BeginInvoke(TryJumpFileDialogToLastFolder);
            return true;
        }
        if (HotkeyChordMatches(_panelPageScrollUpModifiers) && vkCode == _panelPageScrollUpKey)
        {
            Dispatcher.BeginInvoke(() => ScrollPage(-1));
            return true;
        }
        if (HotkeyChordMatches(_panelPageScrollDownModifiers) && vkCode == _panelPageScrollDownKey)
        {
            Dispatcher.BeginInvoke(() => ScrollPage(1));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Win+V 拦截后吞掉 Win KeyUp，注入 Escape（关闭可能闪出的开始菜单）+ 合成 Win KeyUp（重置系统 Win 键状态）。
    /// </summary>
    private static void InjectWinKeyUpReset(Win32.KBDLLHOOKSTRUCT kb)
    {
        uint ext = (kb.flags & 0x01) != 0 ? Win32.KEYEVENTF_EXTENDEDKEY : 0u;
        var inputs = new Win32.INPUT[3];

        // Escape：关闭可能闪出的开始菜单
        inputs[0].type = Win32.INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = (ushort)Win32.VK_ESCAPE;
        inputs[0].u.ki.wScan = 0;
        inputs[0].u.ki.dwFlags = 0;
        inputs[0].u.ki.time = 0;
        inputs[0].u.ki.dwExtraInfo = IntPtr.Zero;

        inputs[1].type = Win32.INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = (ushort)Win32.VK_ESCAPE;
        inputs[1].u.ki.wScan = 0;
        inputs[1].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        inputs[1].u.ki.time = 0;
        inputs[1].u.ki.dwExtraInfo = IntPtr.Zero;

        // 合成 Win KeyUp：重置系统 Win 键状态
        inputs[2].type = Win32.INPUT_KEYBOARD;
        inputs[2].u.ki.wVk = (ushort)kb.vkCode;
        inputs[2].u.ki.wScan = 0;
        inputs[2].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP | ext;
        inputs[2].u.ki.time = 0;
        inputs[2].u.ki.dwExtraInfo = IntPtr.Zero;

        Win32.SendInput(3, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    private static void InjectSyntheticHotkeyAltChordCleanup(Win32.KBDLLHOOKSTRUCT kb)
    {
        uint ext = (kb.flags & 0x01) != 0 ? Win32.KEYEVENTF_EXTENDEDKEY : 0u;
        var inputs = new Win32.INPUT[3];

        inputs[0].type = Win32.INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = Win32.VK_CONTROL;
        inputs[0].u.ki.wScan = 0;
        inputs[0].u.ki.dwFlags = 0;
        inputs[0].u.ki.time = 0;
        inputs[0].u.ki.dwExtraInfo = IntPtr.Zero;

        inputs[1].type = Win32.INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = Win32.VK_CONTROL;
        inputs[1].u.ki.wScan = 0;
        inputs[1].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        inputs[1].u.ki.time = 0;
        inputs[1].u.ki.dwExtraInfo = IntPtr.Zero;

        inputs[2].type = Win32.INPUT_KEYBOARD;
        inputs[2].u.ki.wVk = (ushort)kb.vkCode;
        inputs[2].u.ki.wScan = 0;
        inputs[2].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP | ext;
        inputs[2].u.ki.time = 0;
        inputs[2].u.ki.dwExtraInfo = IntPtr.Zero;

        Win32.SendInput(3, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

#if CLIPX_FILEJUMP
    private static bool IsSystemExplorerForeground()
    {
        var fg = Win32.GetForegroundWindow();
        return FileManagerPathCollector.TryFindExplorerCabinetFrame(fg) != IntPtr.Zero;
    }
#endif

    private static bool IsPhysicalCtrlDown() =>
        (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0
        || (Win32.GetAsyncKeyState(0xA2) & 0x8000) != 0
        || (Win32.GetAsyncKeyState(0xA3) & 0x8000) != 0;

    private bool AltPhysicallyDown() =>
        ((Win32.GetAsyncKeyState(0x12) & 0x8000) != 0)
        || ((Win32.GetAsyncKeyState(0xA4) & 0x8000) != 0)
        || ((Win32.GetAsyncKeyState(0xA5) & 0x8000) != 0);

    /// <summary>物理 Alt 或主面板吞 Alt Down 后的锁存（吞键后 GetAsyncKeyState(Alt) 常为假，导致 Alt+/ 误进搜索）。</summary>
    private bool AltEffectiveForRegisteredChord() => AltPhysicallyDown() || _swallowedMenuAltLatch;

    /// <summary>
    /// 与 RegisterHotKey 的 fsModifiers 一致；含 <see cref="_swallowedMenuAltLatch"/> 与 AltGr（LCtrl+RAlt）兜底。
    /// </summary>
    private bool HotkeyChordMatches(uint requiredMods)
    {
        bool ctrl = IsPhysicalCtrlDown();
        bool shift = ((Win32.GetAsyncKeyState(0x10) & 0x8000) != 0)
            || ((Win32.GetAsyncKeyState(0xA0) & 0x8000) != 0)
            || ((Win32.GetAsyncKeyState(0xA1) & 0x8000) != 0);
        bool alt = AltEffectiveForRegisteredChord();
        bool win = ((Win32.GetAsyncKeyState(0x5B) & 0x8000) != 0)
            || ((Win32.GetAsyncKeyState(0x5C) & 0x8000) != 0);
        bool reqCtrl = (requiredMods & Win32.MOD_CONTROL) != 0;
        bool reqShift = (requiredMods & Win32.MOD_SHIFT) != 0;
        bool reqAlt = (requiredMods & Win32.MOD_ALT) != 0;
        bool reqWin = (requiredMods & Win32.MOD_WIN) != 0;
        if (ctrl == reqCtrl && shift == reqShift && alt == reqAlt && win == reqWin)
            return true;
        if ((requiredMods & Win32.MOD_ALT) == 0 || (requiredMods & Win32.MOD_CONTROL) != 0)
            return false;
        bool physAlt = AltPhysicallyDown();
        if (shift != reqShift || win != reqWin)
            return false;
        return physAlt && IsPhysicalCtrlDown();
    }

    private bool IsPanelModifierDown()
    {
        bool ctrl = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool alt = (Win32.GetAsyncKeyState(0x12) & 0x8000) != 0;
        bool win = ((Win32.GetAsyncKeyState(0x5B) | Win32.GetAsyncKeyState(0x5C)) & 0x8000) != 0;
        bool caps = (Win32.GetAsyncKeyState(0x14) & 0x8000) != 0;

        return _panelModifierKey switch
        {
            "Alt" => alt && !ctrl,
            "Win" => win && !ctrl && !alt,
            "CapsLock" => caps && !ctrl && !alt,
            _ => ctrl && !alt,
        };
    }

    private string PanelModifierDisplayName => _panelModifierKey switch
    {
        "Alt" => "Alt",
        "Win" => "Win",
        "CapsLock" => "CapsLk",
        _ => "Ctrl",
    };

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var msg = wParam.ToInt32();
        var kb = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
        var isKeyDown = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
        var isKeyUp = msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;

        // 拦截 Win+V（系统剪贴板历史快捷键），替换为 ClipboardX
        // Win 键 (0x5B/0x5C) + V 键 (0x56)
        // 仅当设置中启用了 ReplaceSystemWinV 时才拦截
        if (isKeyDown && kb.vkCode == Win32.VK_V && (_appSettings?.ReplaceSystemWinV ?? false))
        {
            bool winDown = (Win32.GetAsyncKeyState(0x5B) & 0x8000) != 0
                || (Win32.GetAsyncKeyState(0x5C) & 0x8000) != 0;
            bool ctrlDown = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0
                || (Win32.GetAsyncKeyState(0xA2) & 0x8000) != 0
                || (Win32.GetAsyncKeyState(0xA3) & 0x8000) != 0;

            // Win+V 且没有按住 Ctrl（避免拦截 Ctrl+Win+V 等其他组合）
            if (winDown && !ctrlDown)
            {
#if CLIPX_CLIPBOARD
                // 触发剪贴板弹窗
                Dispatcher.BeginInvoke(TogglePopup);
#endif
                _winVIntercepted = true;
                // 拦截按键，不传递给系统
                return (IntPtr)1;
            }
        }

        // Win+V 拦截后：吞掉 Win KeyUp 防止开始菜单弹出，然后注入 Escape（关闭可能闪出的开始菜单）
        // + 合成 Win KeyUp（重置系统 Win 键状态，避免 Win 键"卡住"）
        if (isKeyUp && _winVIntercepted && (kb.vkCode == 0x5B || kb.vkCode == 0x5C))
        {
            _winVIntercepted = false;
            InjectWinKeyUpReset(kb);
            return (IntPtr)1;
        }

#if CLIPX_CLIPBOARD
        // 非本窗口内 Ctrl+V 松 V、或 Shift+Insert 松 Insert：FIFO/LIFO 出队并写下一项到剪贴板；不拦截按键
        if (isKeyUp)
        {
            var injEarly = (kb.flags & (Win32.LLKHF_INJECTED | Win32.LLKHF_LOWER_IL_INJECTED)) != 0;
            if (!injEarly && !_isSettingClipboard && !_pasteInProgress && _activeFileJumpPicker == null)
            {
                bool ctrlNow = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0
                    || (Win32.GetAsyncKeyState(0xA2) & 0x8000) != 0
                    || (Win32.GetAsyncKeyState(0xA3) & 0x8000) != 0;
                bool shiftNow = (Win32.GetAsyncKeyState(0x10) & 0x8000) != 0
                    || (Win32.GetAsyncKeyState(0xA0) & 0x8000) != 0
                    || (Win32.GetAsyncKeyState(0xA1) & 0x8000) != 0;
                bool pasteChord =
                    (kb.vkCode == Win32.VK_V && ctrlNow)
                    || (kb.vkCode == Win32.VK_INSERT && shiftNow);
                if (pasteChord)
                {
                    var fg = Win32.GetForegroundWindow();
                    if (fg != IntPtr.Zero && fg != _hwnd)
                    {
                        long tick = Environment.TickCount64;
                        if (tick - _lastGlobalPasteQueueAdvanceTick > 120)
                        {
                            if ((GetBatchMode() == BatchPasteQueueMode.Fifo || GetBatchMode() == BatchPasteQueueMode.Lifo)
                                && _batchQueue.Count == 0
                                && (_appSettings?.BatchQueueAutoSwitchToNormalAfterQueueDone ?? true)
                                && _batchQueueAwaitingNextPasteToSwitchOff)
                            {
                                _lastGlobalPasteQueueAdvanceTick = tick;
                                Dispatcher.BeginInvoke(() => SetBatchPasteMode(BatchPasteQueueMode.Off));
                            }
                            else if (GetBatchMode() != BatchPasteQueueMode.Off && _batchQueue.Count > 0)
                            {
                                _lastGlobalPasteQueueAdvanceTick = tick;
                                Dispatcher.BeginInvoke(new Action(TryAdvancePasteQueueAfterGlobalPaste));
                            }
                        }
                    }
                }
            }
        }
#endif

        // 面板未关闭时钩子仍会吃掉未识别的键；多段粘贴中间几次不走 HidePopup，SendInput 的 Shift+Insert 必须放行。
        if ((kb.flags & (Win32.LLKHF_INJECTED | Win32.LLKHF_LOWER_IL_INJECTED)) != 0)
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        TryExpireHotkeyAltChordCleanupDeadline();

        if (!_isPopupVisible && _awaitHotkeyAltChordCleanup)
        {
            if (_activeFileJumpPicker != null)
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

#if CLIPX_FILEJUMP
            // 剪贴板浮层仍显示但焦点已在系统资源管理器时，必须放行低级键盘链，
            // 否则后装的钩子无法收到按键（资源管理器内 Everything 筛选等）。
            if (IsSystemExplorerForeground())
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
#endif

            if (isKeyUp && IsMenuAltVk(kb.vkCode) && !_ctxAltAwaitRelease)
            {
                _awaitHotkeyAltChordCleanup = false;
                _hotkeyAltChordCleanupDeadlineTick = 0;
                _ctxAltComboDuringRelease = false;
                InjectSyntheticHotkeyAltChordCleanup(kb);
#if CLIPX_CLIPBOARD
                SyncBatchPasteKeyboardHook();
#else
                if (!_isPopupVisible)
                    UninstallKeyboardHook();
#endif
                return (IntPtr)1;
            }

            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        if (!_isPopupVisible)
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        if (_activeFileJumpPicker != null)
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        if (TextEntryEditPopup.IsOpen)
        {
            if (isKeyDown)
            {
                if (kb.vkCode == Win32.VK_ESCAPE)
                {
                    Dispatcher.BeginInvoke(CancelEntryTextEdit);
                    return (IntPtr)1;
                }

                if (kb.vkCode == Win32.VK_RETURN && IsPhysicalCtrlDown())
                {
                    Dispatcher.BeginInvoke(CommitEntryTextEdit);
                    return (IntPtr)1;
                }
            }

            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        if (isKeyUp && IsMenuAltVk(kb.vkCode))
        {
            _swallowedMenuAltLatch = false;
            if (PhraseEditPopup.IsOpen)
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            if (_awaitHotkeyAltChordCleanup && !_ctxAltAwaitRelease)
            {
                _awaitHotkeyAltChordCleanup = false;
                _hotkeyAltChordCleanupDeadlineTick = 0;
                _ctxAltComboDuringRelease = false;
                InjectSyntheticHotkeyAltChordCleanup(kb);
                return (IntPtr)1;
            }

            if (BatchMenuPopup.IsOpen)
            {
                if (_ctxAltCloseMenuArmed && !_ctxAltComboDuringRelease)
                    Dispatcher.BeginInvoke(() => { BatchMenuPopup.IsOpen = false; CloseBatchMenuNavUi(); });
                _ctxAltCloseMenuArmed = false;
                _ctxAltAwaitRelease = false;
                _ctxAltComboDuringRelease = false;
                return (IntPtr)1;
            }

            if (ContextPopup.IsOpen)
            {
                if (_ctxAltCloseMenuArmed && !_ctxAltComboDuringRelease)
                    Dispatcher.BeginInvoke(CloseContextMenuPopup);
                _ctxAltCloseMenuArmed = false;
                _ctxAltAwaitRelease = false;
                _ctxAltComboDuringRelease = false;
                return (IntPtr)1;
            }

            if (_ctxAltAwaitRelease && !_ctxAltComboDuringRelease)
                Dispatcher.BeginInvoke(TryOpenBatchOrContextMenuFromKeyboard);
            _ctxAltAwaitRelease = false;
            _ctxAltComboDuringRelease = false;
            // 吞掉 Alt 松开，避免宿主在未收到 Down 时仍收到 Up，或双次触发菜单栏
            return (IntPtr)1;
        }

        if (!isKeyDown)
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        if (PhraseEditPopup.IsOpen)
        {
            if (kb.vkCode == Win32.VK_ESCAPE)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    PhraseEditPopup.IsOpen = false;
                    _phraseEditEntry = null;
                    _phraseEditBuffer = "";
                });
                return (IntPtr)1;
            }
            if (kb.vkCode == Win32.VK_RETURN)
            {
                Dispatcher.BeginInvoke(CommitPhraseEdit);
                return (IntPtr)1;
            }

            if (kb.vkCode is 0x10 or 0x11 or 0x14
                or 0xA0 or 0xA1 or 0xA2 or 0xA3
                or 0x5B or 0x5C)
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            bool phraseCtrlDown = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool phraseAltDown = ((Win32.GetAsyncKeyState(0x12) & 0x8000) != 0)
                || ((Win32.GetAsyncKeyState(0xA4) & 0x8000) != 0)
                || ((Win32.GetAsyncKeyState(0xA5) & 0x8000) != 0);
            bool phraseWinDown = ((Win32.GetAsyncKeyState(0x5B) & 0x8000) != 0)
                || ((Win32.GetAsyncKeyState(0x5C) & 0x8000) != 0);
            if (phraseCtrlDown || phraseAltDown || phraseWinDown)
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            if (kb.vkCode == Win32.VK_BACK)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_phraseEditBuffer.Length > 0)
                        _phraseEditBuffer = _phraseEditBuffer[..^1];
                    RefreshPhraseEditDisplay();
                });
                return (IntPtr)1;
            }

            if (kb.vkCode == 0x09)
                return (IntPtr)1;

            if (kb.vkCode == Win32.VK_SPACE)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_phraseEditBuffer.Length < PhraseEditMaxLen)
                        _phraseEditBuffer += " ";
                    RefreshPhraseEditDisplay();
                });
                return (IntPtr)1;
            }

            if (kb.vkCode is Win32.VK_UP or Win32.VK_DOWN or Win32.VK_LEFT or Win32.VK_RIGHT
                or Win32.VK_HOME or Win32.VK_END or Win32.VK_PRIOR or Win32.VK_NEXT
                or Win32.VK_DELETE)
                return (IntPtr)1;

            var pch = VkToChar(kb.vkCode, kb.scanCode);
            if (pch.HasValue && _phraseEditBuffer.Length < PhraseEditMaxLen)
                Dispatcher.BeginInvoke(() =>
                {
                    _phraseEditBuffer += pch.Value;
                    RefreshPhraseEditDisplay();
                });
            return (IntPtr)1;
        }

        if (BatchMenuPopup.IsOpen)
        {
            if (IsMenuAltVk(kb.vkCode))
            {
                _ctxAltCloseMenuArmed = true;
                _ctxAltComboDuringRelease = false;
                return (IntPtr)1;
            }

            if (TryDispatchRegisteredAppHotkeyChordFromHook(kb.vkCode))
                return (IntPtr)1;

            bool altPhyB = ((Win32.GetAsyncKeyState(0x12) & 0x8000) != 0)
                || ((Win32.GetAsyncKeyState(0xA4) & 0x8000) != 0)
                || ((Win32.GetAsyncKeyState(0xA5) & 0x8000) != 0);
            if (_ctxAltCloseMenuArmed && altPhyB)
                _ctxAltComboDuringRelease = true;

            switch (kb.vkCode)
            {
                case Win32.VK_UP:
                    Dispatcher.BeginInvoke(() => MoveBatchMenuHighlight(-1));
                    return (IntPtr)1;
                case Win32.VK_DOWN:
                    Dispatcher.BeginInvoke(() => MoveBatchMenuHighlight(1));
                    return (IntPtr)1;
                case Win32.VK_RETURN:
                    Dispatcher.BeginInvoke(ActivateBatchMenuHighlight);
                    return (IntPtr)1;
                case Win32.VK_ESCAPE:
                    Dispatcher.BeginInvoke(() => { BatchMenuPopup.IsOpen = false; CloseBatchMenuNavUi(); });
                    return (IntPtr)1;
                default:
                    return (IntPtr)1;
            }
        }

        if (ContextPopup.IsOpen)
        {
            if (IsMenuAltVk(kb.vkCode))
            {
                _ctxAltCloseMenuArmed = true;
                _ctxAltComboDuringRelease = false;
                return (IntPtr)1;
            }

            if (TryDispatchRegisteredAppHotkeyChordFromHook(kb.vkCode))
                return (IntPtr)1;

            bool altPhy = ((Win32.GetAsyncKeyState(0x12) & 0x8000) != 0)
                || ((Win32.GetAsyncKeyState(0xA4) & 0x8000) != 0)
                || ((Win32.GetAsyncKeyState(0xA5) & 0x8000) != 0);
            if (_ctxAltCloseMenuArmed && altPhy)
                _ctxAltComboDuringRelease = true;

            switch (kb.vkCode)
            {
                case Win32.VK_UP:
                    Dispatcher.BeginInvoke(() => MoveContextMenuHighlight(-1));
                    return (IntPtr)1;
                case Win32.VK_DOWN:
                    Dispatcher.BeginInvoke(() => MoveContextMenuHighlight(1));
                    return (IntPtr)1;
                case Win32.VK_RETURN:
                    Dispatcher.BeginInvoke(ActivateContextMenuHighlight);
                    return (IntPtr)1;
                case Win32.VK_ESCAPE:
                    Dispatcher.BeginInvoke(CloseContextMenuPopup);
                    return (IntPtr)1;
                default:
                    return (IntPtr)1;
            }
        }

        if (IsMenuAltVk(kb.vkCode) && !PhraseEditPopup.IsOpen && !TextEntryEditPopup.IsOpen && !ContextPopup.IsOpen && !BatchMenuPopup.IsOpen)
        {
            _swallowedMenuAltLatch = true;
            if (!_awaitHotkeyAltChordCleanup)
            {
                _ctxAltAwaitRelease = true;
                _ctxAltComboDuringRelease = false;
            }
            // 吞掉 Alt 按下，不透传到宿主，避免 Word/浏览器等抢菜单焦点、Access Key 导致剪贴板面板失焦。
            // 单按 Alt 松开后仍由上方 KeyUp 分支打开本面板右键/批量菜单（_ctxAltAwaitRelease）。
            return (IntPtr)1;
        }

        if (_ctxAltAwaitRelease && !IsMenuAltVk(kb.vkCode))
            _ctxAltComboDuringRelease = true;

        if (kb.vkCode is 0x10 or 0x11 or 0x14
            or 0xA0 or 0xA1 or 0xA2 or 0xA3
            or 0x5B or 0x5C)
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        if (HotkeyChordMatches(_panelPageScrollUpModifiers) && kb.vkCode == _panelPageScrollUpKey)
        {
            Dispatcher.BeginInvoke(() => ScrollPage(-1));
            return (IntPtr)1;
        }

        if (HotkeyChordMatches(_panelPageScrollDownModifiers) && kb.vkCode == _panelPageScrollDownKey)
        {
            Dispatcher.BeginInvoke(() => ScrollPage(1));
            return (IntPtr)1;
        }

        // 必须在 IsPanelModifierDown / ctrlHeld||altHeld 放行之前：否则 Alt+`、Alt+/ 会 CallNextHookEx 进搜索框打出字符。
        if (TryDispatchRegisteredAppHotkeyChordFromHook(kb.vkCode))
            return (IntPtr)1;

        bool ctrlHeld = IsPhysicalCtrlDown();
        bool altHeld = AltEffectiveForRegisteredChord();

        if (IsPanelModifierDown())
        {
            if (kb.vkCode >= 0x31 && kb.vkCode <= 0x39)
            {
                int idx = (int)(kb.vkCode - 0x30);
                Dispatcher.BeginInvoke(() => PasteByIndex(idx));
                return (IntPtr)1;
            }
            if (kb.vkCode == 0x09)
            {
                Dispatcher.BeginInvoke(ToggleQuickPhraseFilter);
                return (IntPtr)1;
            }
            if (kb.vkCode == Win32.VK_RETURN)
            {
                bool ctrlEnter = (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0;
                Dispatcher.BeginInvoke(() => HandleMainEnterKey(ctrlEnter));
                return (IntPtr)1;
            }
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        // Enter：不等同于「组合键放行」。Ctrl+点击多选后常仍按住 Ctrl 再按回车，必须在钩子内拦截。
        if (kb.vkCode == Win32.VK_RETURN)
        {
            bool ctrlEnter = (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0;
            Dispatcher.BeginInvoke(() => HandleMainEnterKey(ctrlEnter));
            return (IntPtr)1;
        }

        // Alt+ 注册热键已在上方 TryDispatch 处理。其余纯 Alt 组合吞掉，避免误入搜索框或激活宿主菜单；
        // Ctrl+Alt 放行；Alt+Tab / Alt+F4 尽量交给系统。
        if (ctrlHeld && altHeld)
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        if (altHeld)
        {
            if (kb.vkCode is 0x09 or 0x73)
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            return (IntPtr)1;
        }
        if (ctrlHeld)
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        switch (kb.vkCode)
        {
            case Win32.VK_SPACE:
                Dispatcher.BeginInvoke(ToggleEntryPreviewBubble);
                return (IntPtr)1;
            case Win32.VK_UP:
                Dispatcher.BeginInvoke(() =>
                {
                    bool shift = (Win32.GetAsyncKeyState(0x10) & 0x8000) != 0
                        || (Win32.GetAsyncKeyState(0xA0) & 0x8000) != 0
                        || (Win32.GetAsyncKeyState(0xA1) & 0x8000) != 0;
                    if (shift)
                        MoveSelectionExtend(-1);
                    else
                        MoveSelection(-1);
                });
                return (IntPtr)1;
            case Win32.VK_DOWN:
                Dispatcher.BeginInvoke(() =>
                {
                    bool shift = (Win32.GetAsyncKeyState(0x10) & 0x8000) != 0
                        || (Win32.GetAsyncKeyState(0xA0) & 0x8000) != 0
                        || (Win32.GetAsyncKeyState(0xA1) & 0x8000) != 0;
                    if (shift)
                        MoveSelectionExtend(1);
                    else
                        MoveSelection(1);
                });
                return (IntPtr)1;
            case Win32.VK_HOME:
                Dispatcher.BeginInvoke(MoveSelectionToFirst);
                return (IntPtr)1;
            case Win32.VK_END:
                Dispatcher.BeginInvoke(MoveSelectionToLast);
                return (IntPtr)1;
            case Win32.VK_PRIOR:
                Dispatcher.BeginInvoke(() => ScrollPage(-1));
                return (IntPtr)1;
            case Win32.VK_NEXT:
                Dispatcher.BeginInvoke(() => ScrollPage(1));
                return (IntPtr)1;
            case Win32.VK_LEFT:
                Dispatcher.BeginInvoke(() => ScrollPage(-1));
                return (IntPtr)1;
            case Win32.VK_RIGHT:
                Dispatcher.BeginInvoke(() => ScrollPage(1));
                return (IntPtr)1;
            case Win32.VK_ESCAPE:
                Dispatcher.BeginInvoke(() =>
                {
                    if (BatchMenuPopup.IsOpen)
                    {
                        BatchMenuPopup.IsOpen = false;
                        CloseBatchMenuNavUi();
                        return;
                    }
                    if (ContextPopup.IsOpen)
                    {
                        CloseContextMenuPopup();
                        return;
                    }
                    if (ShortcutHelpPopup.IsOpen)
                    {
                        ShortcutHelpPopup.IsOpen = false;
                        return;
                    }
                    if (EntryPreviewPopup.IsOpen)
                    {
                        CloseEntryPreviewBubble();
                        return;
                    }
                    if (_pendingDeleteEntry != null)
                    {
                        ClearPendingDelete();
                        return;
                    }
                    if (_searchText.Length > 0) { _searchText = ""; RefreshFilter(); }
                    else HidePopup();
                });
                return (IntPtr)1;
            case Win32.VK_BACK:
                Dispatcher.BeginInvoke(() =>
                {
                    if (_searchText.Length > 0) { _searchText = _searchText[..^1]; RefreshFilter(); }
                });
                return (IntPtr)1;
            case 0x09:
                Dispatcher.BeginInvoke(CycleTypeFilter);
                return (IntPtr)1;
            case Win32.VK_DELETE:
                Dispatcher.BeginInvoke(DeleteSelectedItemWithConfirm);
                return (IntPtr)1;
        }

        var ch = VkToChar(kb.vkCode, kb.scanCode);
        if (ch.HasValue)
        {
            if (AltEffectiveForRegisteredChord())
                return (IntPtr)1;
            Dispatcher.BeginInvoke(() => { _searchText += ch.Value; RefreshFilter(); });
        }

        return (IntPtr)1;
    }
}
