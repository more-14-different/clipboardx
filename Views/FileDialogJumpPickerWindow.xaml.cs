using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
    private static readonly Win32.LowLevelKeyboardProc s_jumpPickerKbThunk = StaticJumpPickerKeyboardHook;
    private static readonly Win32.LowLevelMouseProc s_jumpPickerMouseThunk = StaticJumpPickerMouseHook;
    private static readonly Win32.WinEventDelegate s_jumpPickerWinEventThunk = StaticJumpPickerWinEventProc;
    private static IntPtr s_jumpPickerKbHookForNext;
    private static FileDialogJumpPickerWindow? s_jumpPickerKbOwner;
    private static IntPtr s_jumpPickerMouseHookForNext;
    private static FileDialogJumpPickerWindow? s_jumpPickerMouseOwner;
    private static FileDialogJumpPickerWindow? s_jumpPickerWinEventOwner;

    private readonly IntPtr _fileDialogOwnerHwnd;
    private readonly int _mouseScreenX;
    private readonly int _mouseScreenY;
    private readonly AppSettings _settings;
    private readonly List<FileJumpCandidate> _collectorSnapshot;

    private IntPtr _jumpKeyboardHook;
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

    private int _pendingPhysX;
    private int _pendingPhysY;
    private bool _snappedPhysicalOnce;
    private string _searchText = "";
    private bool _favoritesOnly;
    private int _firstVisibleIndex;

    private readonly List<FileJumpPickerRow> _masterRows = new();
    private readonly ObservableCollection<FileJumpPickerRow> _displayRows = new();

    public string? SelectedPath { get; private set; }

    public FileDialogJumpPickerWindow(
        IReadOnlyList<FileJumpCandidate> collectorItems,
        int preferSelectedIndex,
        int mouseScreenX,
        int mouseScreenY,
        AppSettings settings,
        IntPtr fileDialogOwnerHwnd)
    {
        _fileDialogOwnerHwnd = fileDialogOwnerHwnd;
        _mouseScreenX = mouseScreenX;
        _mouseScreenY = mouseScreenY;
        _settings = settings;
        _collectorSnapshot = collectorItems.ToList();

        InitializeComponent();
        Opacity = 0;
        ItemsList.ItemsSource = _displayRows;
        FileJumpHintText.Text =
            $"右键可收藏/移除；再按 {_settings.FileJumpHotkeyDisplayName} 同主面板逻辑。";

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
        UninstallKeyboardHook();
        UninstallJumpPickerOutsideHooks();
        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                HwndSource.FromHwnd(_hwnd)?.RemoveHook(JumpPickerWndProc);
            }
            catch { /* ignore */ }
        }
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
                Win32.SetForegroundWindow(hwnd);
                Win32.SetFocus(hwnd);
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

    private static IntPtr StaticJumpPickerKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var owner = s_jumpPickerKbOwner;
        var hhk = s_jumpPickerKbHookForNext;
        if (owner != null && hhk != IntPtr.Zero)
            return owner.JumpKeyboardHookProc(nCode, wParam, lParam);
        return Win32.CallNextHookEx(hhk, nCode, wParam, lParam);
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
        Dispatcher.BeginInvoke(() => TryDismissJumpPickerFromForegroundChange(hwnd));
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
        if (newForeground == _hwnd) return;
        Win32.GetCursorPos(out var cursor);
        if (Win32.WindowFromPoint(cursor) == _hwnd) return;
        if (JumpRowContextMenu.IsOpen) return;
        if (_suppressDismissForSubDialog) return;
        Close();
    }

    private void InstallKeyboardHook()
    {
        if (_jumpKeyboardHook != IntPtr.Zero) return;
        s_jumpPickerKbOwner = this;
        _jumpKeyboardHook = Win32.SetWindowsHookEx(
            Win32.WH_KEYBOARD_LL, s_jumpPickerKbThunk, Win32.GetModuleHandle(null), 0);
        s_jumpPickerKbHookForNext = _jumpKeyboardHook;
    }

    private void UninstallKeyboardHook()
    {
        if (_jumpKeyboardHook == IntPtr.Zero) return;
        Win32.UnhookWindowsHookEx(_jumpKeyboardHook);
        _jumpKeyboardHook = IntPtr.Zero;
        if (s_jumpPickerKbOwner == this)
        {
            s_jumpPickerKbOwner = null;
            s_jumpPickerKbHookForNext = IntPtr.Zero;
        }
    }

    private IntPtr JumpKeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return Win32.CallNextHookEx(_jumpKeyboardHook, nCode, wParam, lParam);
        if (wParam != (IntPtr)Win32.WM_KEYDOWN)
            return Win32.CallNextHookEx(_jumpKeyboardHook, nCode, wParam, lParam);
        if (_suppressJumpHook)
            return Win32.CallNextHookEx(_jumpKeyboardHook, nCode, wParam, lParam);

        var kb = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);

        bool handled = false;
        try
        {
            Dispatcher.Invoke(() => { handled = ApplyKeyDown(kb.vkCode, kb.scanCode); });
        }
        catch
        {
            return Win32.CallNextHookEx(_jumpKeyboardHook, nCode, wParam, lParam);
        }

        return handled
            ? (IntPtr)1
            : Win32.CallNextHookEx(_jumpKeyboardHook, nCode, wParam, lParam);
    }

    /// <summary>统一键盘逻辑：低级钩子同步调用；返回 true 表示已消费按键。</summary>
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
                    CommitAndClose(r.Path);
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

    private void RefreshFilter(int? preferListIndex = null)
    {
        _displayRows.Clear();
        _firstVisibleIndex = 0;

        IEnumerable<FileJumpPickerRow> seq = _masterRows;
        if (_favoritesOnly)
            seq = seq.Where(r => r.IsFavorite);

        var query = _searchText.Trim();
        if (!string.IsNullOrEmpty(query))
            seq = seq.Where(r => r.MatchesSearch(query));

        var sorted = seq
            .OrderByDescending(r => r.IsFavorite && !string.IsNullOrEmpty(query))
            .ThenByDescending(r => r.IsFavorite)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var r in sorted)
            _displayRows.Add(r);

        AssignVisibleQuickIndices(0);

        UpdateSearchChrome();
        if (_displayRows.Count > 0)
        {
            int sel = preferListIndex.HasValue
                ? Math.Clamp(preferListIndex.Value, 0, _displayRows.Count - 1)
                : 0;
            ItemsList.SelectedIndex = sel;
            ItemsList.ScrollIntoView(ItemsList.SelectedItem);
        }
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
        SearchTextBlock.Text = _searchText;
        SearchCountText.Text = has ? $"{_displayRows.Count} 条结果" : "";
    }

    private void UpdateFooterHints()
    {
        var m = PanelModifierDisplayName(_settings.PanelModifierKey);
        FooterHintsText.Text =
            $"{m}+1~9 跳转 · ↑↓ 选择 · ←→ 翻页 · 单击或 Enter 确认 · Tab · 字母搜索 · 右键收藏 · Esc 取消/清搜索";
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
            if (_fileDialogOwnerHwnd != IntPtr.Zero)
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

        InstallKeyboardHook();
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

    private void ApplyPendingPhysicalSetWindowPos()
    {
        if (_hwnd == IntPtr.Zero) return;
        _isOurSetWindowPosForPicker = true;
        try
        {
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, _pendingPhysX, _pendingPhysY, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
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
            ApplyPendingPhysicalSetWindowPos();
            ApplyPendingPhysicalAsWpfLeftTop();
        }
        catch { /* ignore */ }
        finally
        {
            _lockJumpPickerNomove = false;
        }

        Opacity = 1.0;
        InstallJumpPickerOutsideHooks();
        Dispatcher.BeginInvoke(TryStealFocusForPicker, DispatcherPriority.Input);
    }

    private void ComputePhysicalPosition(bool useActualSize)
    {
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
        var el = e.OriginalSource as DependencyObject;
        while (el != null && el is not ListBoxItem)
            el = VisualTreeHelper.GetParent(el);
        if (el is ListBoxItem lbi && lbi.DataContext is FileJumpPickerRow row)
        {
            ItemsList.SelectedItem = row;
            CommitAndClose(row.Path);
        }
    }

    private void ItemsList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var el = e.OriginalSource as DependencyObject;
        while (el != null && el is not ListBoxItem)
            el = VisualTreeHelper.GetParent(el);
        if (el is ListBoxItem lbi && lbi.DataContext is FileJumpPickerRow row)
            ItemsList.SelectedItem = row;
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
        var phrase = PromptSimpleText("收藏关键词（用于搜索）", def);
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
            CommitAndClose(row.Path);
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

    private void CommitAndClose(string path)
    {
        SelectedPath = path;
        DialogResult = true;
        Close();
    }
}
