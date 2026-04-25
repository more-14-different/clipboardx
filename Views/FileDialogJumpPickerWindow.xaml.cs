using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Media = System.Windows.Media;
using Orientation = System.Windows.Controls.Orientation;

namespace ClipboardManager;

/// <summary>文件对话框跳转：检索、方向键、主键+数字、收藏（设置持久化），交互对齐主剪贴板面板。</summary>
public partial class FileDialogJumpPickerWindow : Window
{
    private const int PageSize = 8;

    /// <summary>低级钩子回调须用静态委托持有，避免 Unhook 后晚到回调撞上已被回收的实例委托（见 <see cref="PopupWindow"/> 说明）。</summary>
    private static readonly Win32.LowLevelMouseProc s_jumpPickerMouseThunk = StaticJumpPickerMouseHook;
    private static readonly Win32.WinEventDelegate s_jumpPickerWinEventThunk = StaticJumpPickerWinEventProc;
    private static IntPtr s_jumpPickerMouseHookForNext;
    private static FileDialogJumpPickerWindow? s_jumpPickerMouseOwner;
    private static FileDialogJumpPickerWindow? s_jumpPickerWinEventOwner;

    private readonly IntPtr _fileDialogOwnerHwnd;
    private readonly int _mouseScreenX;
    private readonly int _mouseScreenY;
    private readonly AppSettings _settings;
    private readonly bool _dockBesideDialog;
    /// <summary>由「对话框成为前台」自动打开：贴靠、不因点文件窗/失焦而关、单击条目只导航不关闭。</summary>
    private readonly bool _autoForegroundStickyMode;
    private readonly List<FileJumpCandidate> _collectorSnapshot;

    private bool _suppressJumpHook;

    private IntPtr _hwnd;
    private bool _isOurSetWindowPosForPicker;
    /// <summary>对齐 <see cref="PopupWindow"/>：首屏阻止 WPF 改坐标，避免先顶部/角落后跳到鼠标处。</summary>
    private bool _lockJumpPickerNomove;

    /// <summary>对齐剪切板弹窗：<see cref="AppSettings.HideOnSameAppClick"/> 时点击跳转窗外关闭。</summary>
    private IntPtr _jumpPickerMouseHook;
    private bool _clickReceivedByJumpPicker;
    private bool _suppressDismissForSubDialog;

    private IntPtr _jumpPickerWinEventHook;

    private DispatcherTimer? _dockFollowTimer;
    private bool _dockDialogRectCached;
    private Win32.RECT _lastDockDialogRect;

    /// <summary>EVENT_SYSTEM_FOREGROUND 在开关窗口时会连发，合并到 UI 线程单次处理，避免队列爆炸导致鼠标卡顿。</summary>
    private int _foregroundDismissCoalesceSeq;

    /// <summary>关闭流程一开始就置位并拆掉 LL 钩，避免关窗前台风暴仍往 UI 线程排队。</summary>
    private volatile bool _jumpPickerInputHooksDetached;

    private int _pendingPhysX;
    private int _pendingPhysY;
    private bool _snappedPhysicalOnce;
    private string _searchText = "";

    /// <summary>列表内搜索高亮绑定用：当前检索词（Trim）。</summary>
    public static readonly DependencyProperty HighlightSearchQueryProperty = DependencyProperty.Register(
        nameof(HighlightSearchQuery),
        typeof(string),
        typeof(FileDialogJumpPickerWindow),
        new PropertyMetadata(""));

    public string HighlightSearchQuery
    {
        get => (string)GetValue(HighlightSearchQueryProperty);
        set => SetValue(HighlightSearchQueryProperty, value ?? "");
    }

    private bool _favoritesOnly;
    private int _firstVisibleIndex;

    /// <summary>粘性模式下点击导航后异步采集；新一次点击递增，过时回调丢弃。</summary>
    private int _commitNavigateKeepOpenGen;

    private readonly List<FileJumpPickerRow> _masterRows = new();
    private readonly ObservableCollection<FileJumpPickerRow> _displayRows = new();

    private readonly List<string> _everythingFolderPaths = new();
    private string _everythingPathsValidForQuery = "";
    private int _everythingQueryGen;
    private CancellationTokenSource? _everythingQueryCts;

    public string? SelectedPath { get; private set; }
    public IntPtr OwnerDialogHwnd => _fileDialogOwnerHwnd;
    public bool IsAutoForegroundStickyMode => _autoForegroundStickyMode;

    public FileDialogJumpPickerWindow(
        IReadOnlyList<FileJumpCandidate> collectorItems,
        int preferSelectedIndex,
        int mouseScreenX,
        int mouseScreenY,
        AppSettings settings,
        IntPtr fileDialogOwnerHwnd,
        bool autoForegroundStickyMode = false)
    {
        _fileDialogOwnerHwnd = fileDialogOwnerHwnd;
        _mouseScreenX = mouseScreenX;
        _mouseScreenY = mouseScreenY;
        _settings = settings;
        _autoForegroundStickyMode = autoForegroundStickyMode;
        // 「对话框打开时自动弹出列表」开启时所有跳转列表（含 Ctrl+G）强制贴对话框；
        // 关闭时按用户「跟随对话框/鼠标」。autoForegroundStickyMode 自身就是自动弹出，必须贴。
        _dockBesideDialog = fileDialogOwnerHwnd != IntPtr.Zero
                            && (autoForegroundStickyMode
                                || settings.FileJumpPickerOpenWhenDialogForeground
                                || FileJumpPickerFollowModes.IsDialog(settings.FileJumpPickerFollowMode));
        _collectorSnapshot = collectorItems.ToList();

        InitializeComponent();
        Opacity = 0;
        ItemsList.ItemsSource = _displayRows;
        FileJumpHintText.Text = autoForegroundStickyMode
            ? $"列表紧贴文件对话框并随窗口移动。单击切换目录；右键收藏/移除；Esc 关闭。快捷键 {_settings.FileJumpHotkeyDisplayName} 与平常相同。"
            : $"右键可收藏/移除；再按 {_settings.FileJumpHotkeyDisplayName} 同主面板逻辑。";

        string? preferPath = null;
        if (preferSelectedIndex >= 0 && preferSelectedIndex < _collectorSnapshot.Count)
            preferPath = _collectorSnapshot[preferSelectedIndex].Path;

        BuildMasterList();
        RefreshFilter();
        if (!string.IsNullOrEmpty(preferPath))
        {
            var idx = _displayRows.ToList().FindIndex(r =>
                string.Equals(r.Path, preferPath, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                ItemsList.SelectedIndex = idx;
                ItemsList.ScrollIntoView(ItemsList.SelectedItem);
            }
        }

        Closed += FileDialogJumpPickerWindow_Closed;
        Activated += FileDialogJumpPickerWindow_Activated;

        SourceInitialized += FileDialogJumpPickerWindow_SourceInitialized;
        ContentRendered += FileDialogJumpPickerWindow_ContentRendered;
        UpdateSearchChrome();
        UpdateFooterHints();
    }

    private void FileDialogJumpPickerWindow_Closed(object? sender, EventArgs e)
    {
        ShellNavigateLog.Write("filejump",
            $"JumpPicker Closed hwnd=0x{_hwnd.ToInt64():X} sticky={_autoForegroundStickyMode}");
        _everythingQueryCts?.Cancel();
        _everythingQueryCts = null;
        DetachJumpPickerLowLevelHooksAndFollowTimer();
        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                HwndSource.FromHwnd(_hwnd)?.RemoveHook(JumpPickerWndProc);
            }
            catch { /* ignore */ }
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var sw = Stopwatch.StartNew();
        _jumpPickerInputHooksDetached = true;
        DetachJumpPickerLowLevelHooksAndFollowTimer();
        sw.Stop();
        ShellNavigateLog.Write("filejump",
            $"JumpPicker OnClosing detach_ms={sw.ElapsedMilliseconds} hwnd=0x{_hwnd.ToInt64():X} sticky={_autoForegroundStickyMode}");
        base.OnClosing(e);
    }

    private void DetachJumpPickerLowLevelHooksAndFollowTimer()
    {
        _dockFollowTimer?.Stop();
        _dockFollowTimer = null;
        UninstallJumpPickerOutsideHooks();
    }

    private void FileDialogJumpPickerWindow_Activated(object? sender, EventArgs e)
    {
        if (IsLoaded)
            Dispatcher.BeginInvoke(TryStealFocusForPicker, DispatcherPriority.Input);
    }

    private void TryStealFocusForPicker()
    {
        try
        {
            Activate();
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                // 另存为等对话框与跳转窗常跨线程；单纯 SetForegroundWindow 易被系统拒绝；与 PopupWindow 夺前台策略一致。
                Win32.SetForegroundWindowAggressive(hwnd);
                try
                {
                    Win32.SetFocus(hwnd);
                }
                catch
                {
                    /* ignore */
                }
            }
        }
        catch { /* ignore */ }
        try
        {
            ItemsList.Focusable = true;
            _ = ItemsList.Focus();
            Keyboard.Focus(ItemsList);
        }
        catch { /* ignore */ }
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        _clickReceivedByJumpPicker = true;
        base.OnPreviewMouseDown(e);
    }

    private void InstallJumpPickerOutsideHooks()
    {
        if (_jumpPickerMouseHook != IntPtr.Zero) return;
        s_jumpPickerMouseOwner = this;
        _jumpPickerMouseHook = Win32.SetWindowsHookEx(
            Win32.WH_MOUSE_LL, s_jumpPickerMouseThunk, Win32.GetModuleHandle(null), 0);
        s_jumpPickerMouseHookForNext = _jumpPickerMouseHook;

        if (_jumpPickerWinEventHook == IntPtr.Zero)
        {
            s_jumpPickerWinEventOwner = this;
            _jumpPickerWinEventHook = Win32.SetWinEventHook(
                Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, s_jumpPickerWinEventThunk, 0, 0,
                Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
        }
    }

    private void UninstallJumpPickerOutsideHooks()
    {
        if (_jumpPickerMouseHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_jumpPickerMouseHook);
            _jumpPickerMouseHook = IntPtr.Zero;
        }
        if (s_jumpPickerMouseOwner == this)
        {
            s_jumpPickerMouseOwner = null;
            s_jumpPickerMouseHookForNext = IntPtr.Zero;
        }

        if (_jumpPickerWinEventHook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_jumpPickerWinEventHook);
            _jumpPickerWinEventHook = IntPtr.Zero;
        }
        if (s_jumpPickerWinEventOwner == this)
            s_jumpPickerWinEventOwner = null;
    }

    private static IntPtr StaticJumpPickerMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var owner = s_jumpPickerMouseOwner;
        var hhk = s_jumpPickerMouseHookForNext;
        if (owner != null && hhk != IntPtr.Zero)
            return owner.JumpPickerMouseHookProc(nCode, wParam, lParam);
        return Win32.CallNextHookEx(hhk, nCode, wParam, lParam);
    }

    private static void StaticJumpPickerWinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        var owner = s_jumpPickerWinEventOwner;
        if (owner != null)
            owner.JumpPickerForegroundCallback(hWinEventHook, eventType, hwnd, idObject, idChild, dwEventThread, dwmsEventTime);
    }

    private IntPtr JumpPickerMouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (_jumpPickerInputHooksDetached)
            return Win32.CallNextHookEx(_jumpPickerMouseHook, nCode, wParam, lParam);
        if (nCode >= 0 && IsLoaded && Opacity > 0)
        {
            var msg = wParam.ToInt32();
            if (msg is Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN)
            {
                if (_settings.HideOnSameAppClick)
                {
                    _clickReceivedByJumpPicker = false;
                    Dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        (Action)(() => TryDismissJumpPickerFromOutsideMouse()));
                }
            }
        }
        return Win32.CallNextHookEx(_jumpPickerMouseHook, nCode, wParam, lParam);
    }

    private void JumpPickerForegroundCallback(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_jumpPickerInputHooksDetached) return;
        // 前台仍在「跳转列表 + 同一文件对话框」会话内时不必 dismiss，也不往 UI 线程派工（关窗时会连发大量前台事件）。
        if (ForegroundHwndKeepsJumpPickerOpen(hwnd)) return;

        int seq = Interlocked.Increment(ref _foregroundDismissCoalesceSeq);
        var evtHwnd = hwnd;
        // 用 Input 优先于 Background，避免主线程上积压的 Input 工作把「关列表」压在队列末尾数秒
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_jumpPickerInputHooksDetached) return;
            if (seq != Volatile.Read(ref _foregroundDismissCoalesceSeq)) return;
            var fg = Win32.GetForegroundWindow();
            ShellNavigateLog.Write("filejump",
                $"JumpPicker sub_fg dismiss_try fg=0x{fg.ToInt64():X} evtHwnd=0x{evtHwnd.ToInt64():X}");
            TryDismissJumpPickerFromForegroundChange(fg);
        });
    }

    /// <summary>前台 HWND 是否属于跳转列表或绑定的文件对话框同一 root（焦点在对话框内不应误关列表）。</summary>
    private bool ForegroundHwndKeepsJumpPickerOpen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (_hwnd != IntPtr.Zero)
        {
            if (hwnd == _hwnd) return true;
            if (Win32.IsChild(_hwnd, hwnd)) return true;
        }

        if (_fileDialogOwnerHwnd == IntPtr.Zero || !Win32.IsWindow(_fileDialogOwnerHwnd)) return false;
        var dlgRoot = Win32.GetAncestor(_fileDialogOwnerHwnd, Win32.GA_ROOT);
        if (dlgRoot == IntPtr.Zero) return false;
        var fgRoot = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        return fgRoot != IntPtr.Zero && fgRoot == dlgRoot;
    }

    private void TryDismissJumpPickerFromOutsideMouse()
    {
        if (!IsLoaded || Opacity <= 0) return;
        if (_clickReceivedByJumpPicker) return;
        if (JumpRowContextMenu.IsOpen) return;
        if (_suppressDismissForSubDialog) return;
        // 勿设 DialogResult：钩子可能在 ShowDialog 完成对话框初始化之前触发，会抛 InvalidOperationException
        Close();
    }

    private void TryDismissJumpPickerFromForegroundChange(IntPtr newForeground)
    {
        if (!IsLoaded || Opacity <= 0) return;
        if (_fileDialogOwnerHwnd != IntPtr.Zero && !Win32.IsWindow(_fileDialogOwnerHwnd))
        {
            Close();
            return;
        }
        if (newForeground == _hwnd) return;
        Win32.GetCursorPos(out var cursor);
        if (Win32.WindowFromPoint(cursor) == _hwnd) return;
        if (JumpRowContextMenu.IsOpen) return;
        if (_suppressDismissForSubDialog) return;
        Close();
    }

    /// <summary>WPF 隧道事件处理列表快捷键；避免 WH_KEYBOARD_LL + Dispatcher.Invoke 阻塞全局输入。</summary>
    private void JumpPicker_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_suppressJumpHook) return;
        if (KeyboardFocusIsExternalEditable()) return;

        int vkInt = KeyInterop.VirtualKeyFromKey(e.Key);
        if (vkInt == 0) return;

        bool ctrl = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0
            || (Win32.GetAsyncKeyState(0xA2) & 0x8000) != 0
            || (Win32.GetAsyncKeyState(0xA3) & 0x8000) != 0;
        bool alt = (Win32.GetAsyncKeyState(0x12) & 0x8000) != 0
            || (Win32.GetAsyncKeyState(0xA4) & 0x8000) != 0
            || (Win32.GetAsyncKeyState(0xA5) & 0x8000) != 0;
        if (ctrl || alt)
        {
            if (!IsPanelModifierMatch())
                return;
        }

        if (ApplyKeyDown((uint)vkInt, 0))
            e.Handled = true;
    }

    /// <summary>
    /// 前台线程焦点在「另存为」等系统对话框的文件名编辑框时，不把按键留给跳转列表，
    /// 便于在保持跳转面板打开的同时修改文件名。
    /// </summary>
    private bool KeyboardFocusIsExternalEditable()
    {
        if (_hwnd == IntPtr.Zero) return false;
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        uint tid = Win32.GetWindowThreadProcessId(fg, out _);
        var gti = new Win32.GUITHREADINFO { cbSize = Marshal.SizeOf<Win32.GUITHREADINFO>() };
        if (!Win32.GetGUIThreadInfo(tid, ref gti) || gti.hwndFocus == IntPtr.Zero)
            return false;
        if (IsFocusWithinJumpPicker(gti.hwndFocus))
            return false;
        return IsEditableTextHwnd(gti.hwndFocus);
    }

    private bool IsFocusWithinJumpPicker(IntPtr hwndFocus)
    {
        for (IntPtr h = hwndFocus; h != IntPtr.Zero; h = Win32.GetParent(h))
        {
            if (h == _hwnd) return true;
        }
        return false;
    }

    private static bool IsEditableTextHwnd(IntPtr hwnd)
    {
        string cls = Win32.GetWindowClassName(hwnd);
        if (string.IsNullOrEmpty(cls)) return false;
        if (cls.Equals("Edit", StringComparison.OrdinalIgnoreCase))
        {
            uint style = unchecked((uint)Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64());
            return (style & Win32.ES_READONLY) == 0;
        }
        return cls.Contains("RichEdit", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>统一键盘逻辑（<see cref="JumpPicker_PreviewKeyDown"/>）；返回 true 表示已消费按键。</summary>
    private bool ApplyKeyDown(uint vk, uint scan)
    {
        if (IsPanelModifierMatch())
        {
            if (vk is >= 0x31 and <= 0x39)
            {
                JumpByQuickIndex((int)(vk - 0x30));
                return true;
            }
            if (vk is >= 0x61 and <= 0x69)
            {
                JumpByQuickIndex((int)(vk - 0x60));
                return true;
            }
            if (vk == 0x09)
            {
                ToggleFavoritesFilter();
                return true;
            }
            if (vk is 0xBB or 0x6B)
            {
                ScrollPage(1);
                return true;
            }
            if (vk is 0xBD or 0x6D)
            {
                ScrollPage(-1);
                return true;
            }
        }

        bool ctrl = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool alt = (Win32.GetAsyncKeyState(0x12) & 0x8000) != 0;
        if (ctrl || alt)
            return false;

        switch (vk)
        {
            case Win32.VK_UP:
                MoveSelection(-1);
                return true;
            case Win32.VK_DOWN:
                MoveSelection(1);
                return true;
            case Win32.VK_LEFT:
                ScrollPage(-1);
                return true;
            case Win32.VK_RIGHT:
                ScrollPage(1);
                return true;
            case Win32.VK_RETURN:
                if (ItemsList.SelectedItem is FileJumpPickerRow r)
                    CommitSelection(r.Path);
                return true;
            case Win32.VK_ESCAPE:
                if (_searchText.Length > 0)
                {
                    _searchText = "";
                    RefreshFilter();
                }
                else
                {
                    DialogResult = false;
                    Close();
                }
                return true;
            case Win32.VK_BACK:
                if (_searchText.Length > 0)
                {
                    _searchText = _searchText[..^1];
                    RefreshFilter();
                }
                return true;
            case 0x09:
                ToggleFavoritesFilter();
                return true;
        }

        var ch = VkToChar(vk, scan);
        if (ch.HasValue && !char.IsControl(ch.Value))
        {
            _searchText += ch.Value;
            RefreshFilter();
            return true;
        }

        return false;
    }

    private static char? VkToChar(uint vkCode, uint scanCode)
    {
        var keyState = new byte[256];
        if ((Win32.GetAsyncKeyState(0x10) & 0x8000) != 0) { keyState[0x10] = 0x80; keyState[0xA0] = 0x80; }
        if ((Win32.GetKeyState(0x14) & 0x0001) != 0) keyState[0x14] = 0x01;

        var sb = new StringBuilder(4);
        int result = Win32.ToUnicode(vkCode, scanCode, keyState, sb, sb.Capacity, 0);
        if (result < 0) Win32.ToUnicode(vkCode, scanCode, keyState, sb, sb.Capacity, 0);
        if (result == 1 && !char.IsControl(sb[0])) return sb[0];
        return null;
    }

    private void BuildMasterList()
    {
        _masterRows.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fav in _settings.FolderFavorites)
        {
            if (string.IsNullOrWhiteSpace(fav.Path)) continue;
            try
            {
                var full = Path.GetFullPath(fav.Path.Trim());
                if (!Directory.Exists(full)) continue;
                if (!seen.Add(full)) continue;
                _masterRows.Add(new FileJumpPickerRow("收藏", full, true, fav.Phrase));
            }
            catch { /* ignore */ }
        }

        foreach (var c in _collectorSnapshot)
        {
            if (seen.Contains(c.Path)) continue;
            try
            {
                if (!Directory.Exists(c.Path)) continue;
            }
            catch { continue; }

            seen.Add(c.Path);
            _masterRows.Add(new FileJumpPickerRow(c.Label, c.Path, false));
        }
    }

    private void RefreshFilter(int? preferListIndex = null, string? preferPath = null)
    {
        var keepPath = preferPath ?? (ItemsList.SelectedItem as FileJumpPickerRow)?.Path;
        _displayRows.Clear();
        _firstVisibleIndex = 0;

        var query = _searchText.Trim();
        if (string.IsNullOrEmpty(query) || !_settings.FileJumpPickerEverythingFolderSearch)
        {
            _everythingFolderPaths.Clear();
            _everythingPathsValidForQuery = "";
            _everythingQueryCts?.Cancel();
        }
        else if (!string.Equals(query, _everythingPathsValidForQuery, StringComparison.OrdinalIgnoreCase))
        {
            _everythingFolderPaths.Clear();
            _everythingPathsValidForQuery = "";
        }

        IEnumerable<FileJumpPickerRow> seq = _masterRows;
        if (_favoritesOnly)
            seq = seq.Where(r => r.IsFavorite);

        if (!string.IsNullOrEmpty(query))
            seq = seq.Where(r => r.MatchesSearch(query));

        var sorted = seq
            .OrderByDescending(r => r.IsFavorite && !string.IsNullOrEmpty(query))
            .ThenByDescending(r => r.IsFavorite)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var r in sorted)
            _displayRows.Add(r);

        if (!string.IsNullOrEmpty(query)
            && _settings.FileJumpPickerEverythingFolderSearch
            && string.Equals(query, _everythingPathsValidForQuery, StringComparison.OrdinalIgnoreCase)
            && _everythingFolderPaths.Count > 0)
        {
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _displayRows)
                seenPaths.Add(r.Path);

            foreach (var p in _everythingFolderPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!seenPaths.Add(p)) continue;
                _displayRows.Add(new FileJumpPickerRow("everything", p, false));
            }
        }

        AssignVisibleQuickIndices(0);

        UpdateSearchChrome();
        if (_displayRows.Count > 0)
        {
            int sel = 0;
            if (!string.IsNullOrEmpty(keepPath))
            {
                var i = _displayRows.ToList().FindIndex(r =>
                    string.Equals(r.Path, keepPath, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) sel = i;
                else if (preferListIndex.HasValue)
                    sel = Math.Clamp(preferListIndex.Value, 0, _displayRows.Count - 1);
            }
            else if (preferListIndex.HasValue)
                sel = Math.Clamp(preferListIndex.Value, 0, _displayRows.Count - 1);

            ItemsList.SelectedIndex = sel;
            ItemsList.ScrollIntoView(ItemsList.SelectedItem);
        }

        if (!string.IsNullOrEmpty(query) && _settings.FileJumpPickerEverythingFolderSearch)
            ScheduleEverythingFolderQuery(query);
    }

    private void ScheduleEverythingFolderQuery(string queryForSchedule)
    {
        if (!_settings.FileJumpPickerEverythingFolderSearch || string.IsNullOrEmpty(queryForSchedule))
        {
            _everythingQueryCts?.Cancel();
            return;
        }

        if (string.Equals(queryForSchedule, _everythingPathsValidForQuery, StringComparison.OrdinalIgnoreCase))
            return;

        _everythingQueryGen++;
        var gen = _everythingQueryGen;
        _everythingQueryCts?.Cancel();
        _everythingQueryCts = new CancellationTokenSource();
        var tok = _everythingQueryCts.Token;
        var maxResults = Math.Clamp(_settings.ExplorerEverythingQuickFindMaxResults, 1, 2000);

        // 早期 debounce 写到 140ms，对「换一段输入再按 Tab/字母」的交互而言体感很重。
        // Everything IPC 文件夹检索单次开销通常 <10ms，节流 40ms 足以合并连按又不感知卡顿。
        _ = Task.Run(() =>
        {
            try
            {
                if (tok.WaitHandle.WaitOne(40)) return;
                if (gen != _everythingQueryGen) return;

                var list = new List<string>();
                var ok = EverythingIpc.TryQueryFolderPaths(queryForSchedule, maxResults, list, out _);

                Dispatcher.BeginInvoke(() =>
                {
                    if (gen != _everythingQueryGen) return;
                    if (!string.Equals(_searchText.Trim(), queryForSchedule, StringComparison.Ordinal)) return;

                    _everythingFolderPaths.Clear();
                    if (ok)
                        _everythingFolderPaths.AddRange(list);
                    _everythingPathsValidForQuery = queryForSchedule;

                    var pathKeep = (ItemsList.SelectedItem as FileJumpPickerRow)?.Path;
                    RefreshFilter(preferPath: pathKeep);
                }, DispatcherPriority.Background);
            }
            catch
            {
                /* ignore */
            }
        });
    }

    private void AssignVisibleQuickIndices(int firstVisible)
    {
        for (int i = 0; i < _displayRows.Count; i++)
        {
            int rel = i - firstVisible + 1;
            _displayRows[i].DisplayIndex = rel is >= 1 and <= 9 ? rel : 0;
        }
    }

    private void UpdateSearchChrome()
    {
        var has = _searchText.Length > 0;
        SearchBarPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        SetCurrentValue(HighlightSearchQueryProperty, _searchText.Trim());
        if (has)
        {
            var primary = TryFindResource("PrimaryText") as Media.Brush ?? Media.Brushes.White;
            var accent = TryFindResource("AccentBg") as Media.Brush ?? Media.Brushes.Teal;
            SearchTextBlock.Inlines.Clear();
            SearchHighlightInlines.Append(
                SearchTextBlock.Inlines,
                _searchText,
                _searchText.Trim(),
                primary,
                accent,
                13,
                FontWeights.Normal);
        }
        else
            SearchTextBlock.Inlines.Clear();

        SearchCountText.Text = has ? $"{_displayRows.Count} 条结果" : "";
    }

    private void UpdateFooterHints()
    {
        var m = PanelModifierDisplayName(_settings.PanelModifierKey);
        FooterHintsText.Text =
            $"{m}+1~9 跳转 · ↑↓ 选择 · ←→ 翻页 · Tab · 字母 everything · 右键收藏 · Esc 取消/清除";
    }

    private static string PanelModifierDisplayName(string key) => key switch
    {
        "Alt" => "Alt",
        "Win" => "Win",
        "CapsLock" => "CapsLk",
        _ => "Ctrl",
    };

    private bool IsPanelModifierMatch()
    {
        bool ctrl = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool alt = (Win32.GetAsyncKeyState(0x12) & 0x8000) != 0;
        bool win = ((Win32.GetAsyncKeyState(0x5B) | Win32.GetAsyncKeyState(0x5C)) & 0x8000) != 0;
        bool caps = (Win32.GetAsyncKeyState(0x14) & 0x8000) != 0;

        return _settings.PanelModifierKey switch
        {
            "Alt" => alt && !ctrl,
            "Win" => win && !ctrl && !alt,
            "CapsLock" => caps && !ctrl && !alt,
            _ => ctrl && !alt,
        };
    }

    private void ToggleFavoritesFilter()
    {
        _favoritesOnly = !_favoritesOnly;
        _searchText = "";
        var keepPath = (ItemsList.SelectedItem as FileJumpPickerRow)?.Path;
        RefreshFilter();
        if (!string.IsNullOrEmpty(keepPath))
        {
            var i = _displayRows.ToList().FindIndex(r => string.Equals(r.Path, keepPath, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) ItemsList.SelectedIndex = i;
        }
    }

    private void FileDialogJumpPickerWindow_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle();
            _hwnd = helper.Handle;
            // 粘性贴靠模式若设为文件对话框的 Owned 窗口，系统常把前台/键盘留在宿主对话框，SetForeground 难以生效；本窗已 Topmost。
            if (_fileDialogOwnerHwnd != IntPtr.Zero && !_autoForegroundStickyMode)
                helper.Owner = _fileDialogOwnerHwnd;

            HwndSource.FromHwnd(_hwnd)?.AddHook(JumpPickerWndProc);
        }
        catch { /* ignore */ }

        _lockJumpPickerNomove = true;
        try
        {
            ComputePhysicalPosition(useActualSize: false);
            ApplyPendingPhysicalAsWpfLeftTop();
            ApplyPendingPhysicalSetWindowPos();
            ApplyPendingPhysicalAsWpfLeftTop();
        }
        catch { /* ignore */ }

        ShellNavigateLog.Write("filejump",
            $"JumpPicker SourceInitialized hwnd=0x{_hwnd.ToInt64():X} dlgOwner=0x{_fileDialogOwnerHwnd.ToInt64():X} sticky={_autoForegroundStickyMode}");
    }

    private IntPtr JumpPickerWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_DPICHANGED:
                _isOurSetWindowPosForPicker = false;
                break;
            case Win32.WM_WINDOWPOSCHANGING:
                if (!_isOurSetWindowPosForPicker && _lockJumpPickerNomove)
                {
                    var pos = Marshal.PtrToStructure<Win32.WINDOWPOS>(lParam);
                    pos.flags |= Win32.SWP_NOMOVE;
                    Marshal.StructureToPtr(pos, lParam, false);
                }
                break;
        }
        return IntPtr.Zero;
    }

    private void ApplyPendingPhysicalSetWindowPos(bool noActivate = true)
    {
        if (_hwnd == IntPtr.Zero) return;
        _isOurSetWindowPosForPicker = true;
        try
        {
            var flags = Win32.SWP_NOSIZE | Win32.SWP_NOZORDER;
            if (noActivate) flags |= Win32.SWP_NOACTIVATE;
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, _pendingPhysX, _pendingPhysY, 0, 0, flags);
        }
        finally
        {
            _isOurSetWindowPosForPicker = false;
        }
    }

    private void FileDialogJumpPickerWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (_snappedPhysicalOnce) return;
        _snappedPhysicalOnce = true;

        try
        {
            UpdateLayout();
            ComputePhysicalPosition(useActualSize: true);
            // 首次布局完成：允许 SetWindowPos 顺带激活，避免 SWP_NOACTIVATE 与「Owned 子窗」叠加导致永远无法抢前台。
            ApplyPendingPhysicalSetWindowPos(noActivate: false);
            ApplyPendingPhysicalAsWpfLeftTop();
        }
        catch { /* ignore */ }
        finally
        {
            _lockJumpPickerNomove = false;
        }

        Opacity = 1.0;
        if (!_autoForegroundStickyMode)
            InstallJumpPickerOutsideHooks();
        // 只要有绑定的文件对话框 HWND，就轮询其是否仍有效；否则「跟随鼠标」等非贴靠模式下关系统框后
        // 仅靠 WinEvent + Background 派工与「光标在列表上则不退」会拖到数秒才关跳转列表。
        if (_fileDialogOwnerHwnd != IntPtr.Zero)
        {
            _dockFollowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _dockFollowTimer.Tick += (_, _) => DockFollowTick();
            _dockFollowTimer.Start();
        }

        ShellNavigateLog.Write("filejump",
            $"JumpPicker ContentRendered hwnd=0x{_hwnd.ToInt64():X} outsideHooks={!_autoForegroundStickyMode} dock={_dockBesideDialog}");
        // 弹出后聚焦列表，字母键才能进 everything 筛选；焦点回到文件对话框编辑框时仍由 KeyboardFocusIsExternalEditable 把按键让给对话框。
        Dispatcher.BeginInvoke(TryStealFocusForPicker, DispatcherPriority.Input);
    }

    private void DockFollowTick()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_fileDialogOwnerHwnd != IntPtr.Zero && !Win32.IsWindow(_fileDialogOwnerHwnd))
        {
            Close();
            return;
        }
        if (!_dockBesideDialog) return;
        if (!Win32.GetWindowRect(_fileDialogOwnerHwnd, out var drDlg))
        {
            Close();
            return;
        }
        if (_dockDialogRectCached
            && drDlg.Left == _lastDockDialogRect.Left
            && drDlg.Top == _lastDockDialogRect.Top
            && drDlg.Right == _lastDockDialogRect.Right
            && drDlg.Bottom == _lastDockDialogRect.Bottom)
            return;

        _lastDockDialogRect = drDlg;
        _dockDialogRectCached = true;
        try
        {
            ComputePhysicalPosition(useActualSize: true);
            ApplyPendingPhysicalSetWindowPos();
            ApplyPendingPhysicalAsWpfLeftTop();
        }
        catch { /* ignore */ }
    }

    private void ComputePhysicalPosition(bool useActualSize)
    {
        if (_dockBesideDialog
            && _fileDialogOwnerHwnd != IntPtr.Zero
            && Win32.IsWindow(_fileDialogOwnerHwnd)
            && TryApplyDockedPhysical(useActualSize))
            return;

        const int marginX = 14;
        const int marginY = 10;

        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(_mouseScreenX, _mouseScreenY));
        var work = screen.WorkingArea;

        var hMon = Win32.MonitorFromPoint(
            new Win32.POINT { X = _mouseScreenX, Y = _mouseScreenY },
            Win32.MONITOR_DEFAULTTONEAREST);
        Win32.GetDpiForMonitor(hMon, 0, out uint monDpiX, out uint monDpiY);
        double scaleX = monDpiX / 96.0;
        double scaleY = monDpiY / 96.0;

        double wpfW = useActualSize && ActualWidth > 1 ? ActualWidth : Width;
        double wpfH;
        if (useActualSize && ActualHeight > 1)
            wpfH = ActualHeight;
        else
            wpfH = MaxHeight > 1 && MaxHeight < 900 ? MaxHeight : 400;

        int popupW = (int)Math.Ceiling(wpfW * scaleX);
        int popupH = (int)Math.Ceiling(wpfH * scaleY);

        int x = _mouseScreenX + marginX;
        int y = _mouseScreenY + marginY;

        if (x + popupW > work.Right) x = work.Right - popupW;
        if (y + popupH > work.Bottom) y = _mouseScreenY - popupH - marginY;
        if (x < work.Left) x = work.Left;
        if (y < work.Top) y = work.Top;

        _pendingPhysX = x;
        _pendingPhysY = y;
    }

    /// <summary>按文件对话框计算紧贴物理坐标；失败则返回 false，由调用方回退到鼠标定位。</summary>
    private bool TryApplyDockedPhysical(bool useActualSize)
    {
        Win32.POINT dpiPt = new() { X = _mouseScreenX, Y = _mouseScreenY };
        if (Win32.GetWindowRect(_fileDialogOwnerHwnd, out var drDlg))
        {
            dpiPt.X = (drDlg.Left + drDlg.Right) / 2;
            dpiPt.Y = (drDlg.Top + drDlg.Bottom) / 2;
        }
        var hMon = Win32.MonitorFromPoint(dpiPt, Win32.MONITOR_DEFAULTTONEAREST);
        Win32.GetDpiForMonitor(hMon, 0, out uint monDpiX, out uint monDpiY);
        double scaleX = monDpiX / 96.0;
        double scaleY = monDpiY / 96.0;

        double wpfW = useActualSize && ActualWidth > 1 ? ActualWidth : Width;
        double wpfH;
        if (useActualSize && ActualHeight > 1)
            wpfH = ActualHeight;
        else
            wpfH = MaxHeight > 1 && MaxHeight < 900 ? MaxHeight : 400;

        int popupW = (int)Math.Ceiling(wpfW * scaleX);
        int popupH = (int)Math.Ceiling(wpfH * scaleY);

        if (!FileJumpPickerDockPlacement.TryComputePosition(_fileDialogOwnerHwnd, popupW, popupH, out var px, out var py))
            return false;

        _pendingPhysX = px;
        _pendingPhysY = py;
        return true;
    }

    private void ApplyPendingPhysicalAsWpfLeftTop()
    {
        try
        {
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle();
            var src = HwndSource.FromHwnd(helper.Handle);
            if (src?.CompositionTarget == null) return;

            var dip = src.CompositionTarget.TransformFromDevice.Transform(
                new System.Windows.Point(_pendingPhysX, _pendingPhysY));
            Left = dip.X;
            Top = dip.Y;
        }
        catch { /* ignore */ }
    }

    private void ItemsList_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (FindAncestorListBoxItem(e.OriginalSource as DependencyObject) is { } lbi
            && lbi.DataContext is FileJumpPickerRow row)
        {
            ItemsList.SelectedItem = row;
            CommitSelection(row.Path);
        }
    }

    private void ItemsList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestorListBoxItem(e.OriginalSource as DependencyObject) is { } lbi
            && lbi.DataContext is FileJumpPickerRow row)
            ItemsList.SelectedItem = row;
    }

    /// <summary>
    /// 沿可视/逻辑树向上找最近的 <see cref="ListBoxItem"/>。
    /// 注意：原 <c>OriginalSource</c> 可能是 <see cref="System.Windows.Documents.Run"/> 等
    /// <see cref="ContentElement"/>（来自高亮 TextBlock 的 Inlines），它不是 Visual，
    /// 直接调用 <see cref="VisualTreeHelper.GetParent"/> 会抛
    /// "System.Windows.Documents.Run 不是 Visual 或 Visual3D"。
    /// 因此需要先用 <see cref="LogicalTreeHelper"/> 走到 Visual 节点再切回视觉树。
    /// </summary>
    private static ListBoxItem? FindAncestorListBoxItem(DependencyObject? start)
    {
        var el = start;
        while (el != null && el is not ListBoxItem)
        {
            DependencyObject? parent = null;
            if (el is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                try { parent = VisualTreeHelper.GetParent(el); }
                catch { parent = null; }
            }

            parent ??= LogicalTreeHelper.GetParent(el);
            if (parent == null) return null;
            el = parent;
        }

        return el as ListBoxItem;
    }

    private void JumpRowContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var row = ItemsList.SelectedItem as FileJumpPickerRow;
        CtxAddFavorite.Visibility = row is { IsFavorite: false } ? Visibility.Visible : Visibility.Collapsed;
        CtxRemoveFavorite.Visibility = row is { IsFavorite: true } ? Visibility.Visible : Visibility.Collapsed;
        CtxEditPhrase.Visibility = row is { IsFavorite: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CtxAddFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not FileJumpPickerRow row || row.IsFavorite) return;
        var def = GuessPhraseFromPath(row.Path);
        var phrase = PromptSimpleText("收藏关键词（用于 everything 筛选）", def);
        if (phrase == null) return;
        phrase = phrase.Trim();
        if (string.IsNullOrEmpty(phrase)) phrase = def;

        _settings.FolderFavorites.RemoveAll(f =>
            string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        _settings.FolderFavorites.Add(new FolderFavoriteEntry { Phrase = phrase, Path = row.Path });
        _settings.Save();
        BuildMasterList();
        RefreshFilter();
        var i = _displayRows.ToList().FindIndex(r => string.Equals(r.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) ItemsList.SelectedIndex = i;
    }

    private void CtxRemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not FileJumpPickerRow row || !row.IsFavorite) return;
        _settings.FolderFavorites.RemoveAll(f =>
            string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        BuildMasterList();
        RefreshFilter();
    }

    private void CtxEditPhrase_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not FileJumpPickerRow row || !row.IsFavorite) return;
        var phrase = PromptSimpleText("修改关键词", row.Phrase);
        if (phrase == null) return;
        phrase = phrase.Trim();
        var fav = _settings.FolderFavorites.FirstOrDefault(f =>
            string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        if (fav == null) return;
        fav.Phrase = phrase;
        _settings.Save();
        BuildMasterList();
        RefreshFilter();
        var i = _displayRows.ToList().FindIndex(r => string.Equals(r.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) ItemsList.SelectedIndex = i;
    }

    private static string GuessPhraseFromPath(string path)
    {
        try
        {
            var t = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileName(t);
        }
        catch { return "收藏"; }
    }

    private string? PromptSimpleText(string title, string initial)
    {
        var dlg = new Window
        {
            Title = title,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = TryFindResource("WindowBgBrush") as Media.Brush ?? System.Windows.SystemColors.WindowBrush,
        };
        var tb = new System.Windows.Controls.TextBox
        {
            Text = initial,
            Margin = new Thickness(14, 6, 14, 8),
            FontSize = 13,
            Padding = new Thickness(8, 6, 8, 6),
            Background = TryFindResource("SurfaceBrush") as Media.Brush,
            Foreground = TryFindResource("PrimaryText") as Media.Brush,
            BorderBrush = TryFindResource("ThemeBorder") as Media.Brush,
            CaretBrush = TryFindResource("PrimaryText") as Media.Brush,
        };
        string? result = null;
        var ok = new System.Windows.Controls.Button { Content = "确定", Width = 88, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new System.Windows.Controls.Button { Content = "取消", Width = 88, IsCancel = true };
        ok.Click += (_, _) => { result = tb.Text; dlg.DialogResult = true; };
        cancel.Click += (_, _) => { dlg.DialogResult = false; };
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(14, 4, 14, 14)
        };
        btnRow.Children.Add(ok);
        btnRow.Children.Add(cancel);
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = title,
            Margin = new Thickness(14, 14, 14, 4),
            FontSize = 13,
            Foreground = TryFindResource("PrimaryText") as Media.Brush,
        });
        sp.Children.Add(tb);
        sp.Children.Add(btnRow);
        dlg.Content = sp;
        _suppressJumpHook = true;
        _suppressDismissForSubDialog = true;
        try
        {
            return dlg.ShowDialog() == true ? result?.Trim() : null;
        }
        finally
        {
            _suppressDismissForSubDialog = false;
            _suppressJumpHook = false;
        }
    }

    private void JumpByQuickIndex(int index1To9)
    {
        var row = _displayRows.FirstOrDefault(r => r.DisplayIndex == index1To9);
        if (row != null)
            CommitSelection(row.Path);
    }

    private void MoveSelection(int delta)
    {
        if (_displayRows.Count == 0) return;
        var idx = ItemsList.SelectedIndex + delta;
        if (idx < 0) idx = 0;
        if (idx >= _displayRows.Count) idx = _displayRows.Count - 1;
        ItemsList.SelectedIndex = idx;
        ItemsList.ScrollIntoView(ItemsList.SelectedItem);
    }

    private void ScrollPage(int direction)
    {
        if (_displayRows.Count == 0) return;
        var sv = GetListScrollViewer();
        if (sv == null) return;

        double itemHeight = sv.ExtentHeight / _displayRows.Count;
        if (itemHeight <= 0) return;

        int oldFirstVisible = Math.Max(0, (int)(sv.VerticalOffset / itemHeight));
        int relSelection = Math.Max(0, ItemsList.SelectedIndex - oldFirstVisible);

        double newOffset = sv.VerticalOffset + direction * PageSize * itemHeight;
        newOffset = Math.Max(0, Math.Min(newOffset, sv.ScrollableHeight));
        sv.ScrollToVerticalOffset(newOffset);

        int newFirstVisible = Math.Max(0, (int)(newOffset / itemHeight));
        _firstVisibleIndex = newFirstVisible;
        int newSel = Math.Clamp(newFirstVisible + relSelection, 0, _displayRows.Count - 1);
        ItemsList.SelectedIndex = newSel;

        AssignVisibleQuickIndices(newFirstVisible);
    }

    private ScrollViewer? GetListScrollViewer()
    {
        if (VisualTreeHelper.GetChildrenCount(ItemsList) == 0) return null;
        var border = VisualTreeHelper.GetChild(ItemsList, 0) as Decorator;
        return border?.Child as ScrollViewer;
    }

    private void ItemsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 || _displayRows.Count == 0) return;
        var sv = GetListScrollViewer();
        if (sv == null) return;

        double itemHeight = sv.ExtentHeight / _displayRows.Count;
        if (itemHeight <= 0) return;

        int newFirstVisible = Math.Max(0, (int)(sv.VerticalOffset / itemHeight));
        if (newFirstVisible == _firstVisibleIndex) return;

        int relSelection = Math.Max(0, ItemsList.SelectedIndex - _firstVisibleIndex);
        _firstVisibleIndex = newFirstVisible;

        int newSel = Math.Clamp(newFirstVisible + relSelection, 0, _displayRows.Count - 1);
        ItemsList.SelectedIndex = newSel;

        AssignVisibleQuickIndices(newFirstVisible);
    }

    private void CommitSelection(string path)
    {
        // 选中即跳转，但跳转列表不自动关闭：由 Esc / 点击列表外 / 文件对话框关闭 等显式动作收起。
        CommitNavigateKeepOpen(path);
    }

    /// <summary>
    /// 外部文件管理器路径变化后，刷新当前候选列表并尽量保持用户关注的路径选中。
    /// </summary>
    public void RefreshCandidatesFromExternal(IReadOnlyList<FileJumpCandidate> fresh, string? preferredPath = null)
    {
        var selectedPath = preferredPath;
        if (string.IsNullOrEmpty(selectedPath) && ItemsList.SelectedItem is FileJumpPickerRow row)
            selectedPath = row.Path;
        ApplyNavigateKeepOpenListRefresh(selectedPath ?? "", fresh.ToList());
    }

    /// <summary>
    /// 粘性模式下由外部触发：保持窗口打开，直接导航到目标路径并在完成后刷新列表。
    /// </summary>
    public void NavigateKeepOpenToPath(string path)
    {
        if (!_autoForegroundStickyMode) return;
        if (string.IsNullOrWhiteSpace(path)) return;
        SelectedPath = path;
        CommitNavigateKeepOpen(path);
    }

    private void CommitAndClose(string path)
    {
        SelectedPath = path;
        DialogResult = true;
        Close();
    }

    /// <summary>粘性自动模式：只切换文件对话框目录并刷新列表，不关闭窗口。</summary>
    private void CommitNavigateKeepOpen(string path)
    {
        unchecked { _commitNavigateKeepOpenGen++; }
        var gen = _commitNavigateKeepOpenGen;
        var dlgHwnd = _fileDialogOwnerHwnd;
        var allowInject = _settings.EnableShellNavigateInject;
        var memBefore = _settings.LastFileDialogFolder?.Trim();

        // 导航过程会通过 SendInput 向目标对话框发送键盘事件（Alt+N / Ctrl+A / 路径 / Enter）；
        // 低级键盘钩子仍在运行，会拦截 Enter 并再次 CommitSelection → 死循环。
        // 在导航+采集完成前抑制钩子，让按键直接传递到目标对话框。
        _suppressJumpHook = true;

        void StaWork()
        {
            try
            {
                if (!FileDialogJumpHelper.TryNavigateToFolder(dlgHwnd, path, allowInject))
                    return;

                string? folderAfter = null;
                try
                {
                    if (FileDialogJumpHelper.TryReadCurrentFolder(dlgHwnd, out var folder)
                        && !string.IsNullOrEmpty(folder))
                        folderAfter = folder;
                }
                catch { /* ignore */ }

                var memForCollect = !string.IsNullOrEmpty(folderAfter?.Trim())
                    ? folderAfter.Trim()
                    : memBefore;

                List<FileJumpCandidate> fresh;
                try
                {
                    fresh = FileManagerPathCollector.CollectCandidates(dlgHwnd, memForCollect,
                        recentFolders: _settings.RecentFileDialogFolders);
                }
                catch
                {
                    // 与原先一致：采集失败则不刷新列表，但仍尽量写入当前目录记忆。
                    var folderOnly = folderAfter;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (gen != _commitNavigateKeepOpenGen) return;
                        try
                        {
                            if (!string.IsNullOrEmpty(folderOnly))
                                _settings.PushRecentFileDialogFolder(folderOnly);
                        }
                        catch { /* ignore */ }
                    }, DispatcherPriority.Normal);
                    return;
                }

                var folderForSettings = folderAfter;
                Dispatcher.BeginInvoke(() =>
                {
                    if (gen != _commitNavigateKeepOpenGen) return;
                    try
                    {
                        if (!string.IsNullOrEmpty(folderForSettings))
                            _settings.PushRecentFileDialogFolder(folderForSettings);
                    }
                    catch { /* ignore */ }

                    ApplyNavigateKeepOpenListRefresh(path, fresh);
                }, DispatcherPriority.Normal);
            }
            finally
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (gen == _commitNavigateKeepOpenGen)
                        _suppressJumpHook = false;
                }, DispatcherPriority.Normal);
            }
        }

        var th = new Thread(StaWork)
        {
            IsBackground = true,
            Name = "ClipboardX-JumpPicker-NavigateRefresh",
        };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
    }

    private void ApplyNavigateKeepOpenListRefresh(string committedPath, List<FileJumpCandidate> fresh)
    {
        _collectorSnapshot.Clear();
        _collectorSnapshot.AddRange(fresh);

        BuildMasterList();
        RefreshFilter();
        var i = _displayRows.ToList().FindIndex(r =>
            string.Equals(r.Path, committedPath, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            ItemsList.SelectedIndex = i;
            ItemsList.ScrollIntoView(ItemsList.SelectedItem);
        }
        else if (_displayRows.Count > 0)
        {
            ItemsList.SelectedIndex = 0;
            ItemsList.ScrollIntoView(ItemsList.SelectedItem);
        }

        if (_dockBesideDialog && _hwnd != IntPtr.Zero)
        {
            try
            {
                UpdateLayout();
                ComputePhysicalPosition(useActualSize: true);
                ApplyPendingPhysicalSetWindowPos();
                ApplyPendingPhysicalAsWpfLeftTop();
            }
            catch { /* ignore */ }
        }
    }
}
