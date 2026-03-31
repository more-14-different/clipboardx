using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace ClipboardManager;

public partial class PopupWindow : Window
{
    private const int HotkeyId = 9001;

    private readonly List<ClipboardEntry> _allItems = new();
    private readonly ObservableCollection<ClipboardEntry> _displayItems = new();

    private IntPtr _hwnd;
    private IntPtr _targetWindow;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private IntPtr _winEventHook;
    private Win32.LowLevelKeyboardProc? _hookProc;
    private Win32.LowLevelMouseProc? _mouseHookProc;
    private Win32.WinEventDelegate? _winEventProc;
    private bool _isSettingClipboard;
    private bool _isPopupVisible;
    private string _searchText = "";
    private EntryType? _typeFilter;
    private bool _quickPhraseOnly;
    private ClipboardEntry? _contextEntry;

    private const int PageSize = 8;
    private int _firstVisibleIndex;

    private uint _hotkeyModifiers;
    private uint _hotkeyKey;
    private int _maxItems;
    private string _popupPosition = "Caret";
    private double _popupOpacity = 0.95;
    private bool _hideOnSameAppClick = true;
    private string _panelModifierKey = "Ctrl";
    private bool _isDragging;
    private Win32.POINT _dragLastPt;
    private bool _clickReceivedByPopup;
    private int _pendingPhysX, _pendingPhysY;
    private bool _isOurSetWindowPos;
    private List<QuickPasteEntry> _quickPastes = new();

    public event Action? SettingsRequested;

    public PopupWindow()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _displayItems;
    }

    public void Initialize(AppSettings settings)
    {
        _maxItems = settings.MaxItems;
        _hotkeyModifiers = settings.HotkeyModifiers;
        _hotkeyKey = settings.HotkeyKey;
        _popupPosition = settings.PopupPosition;
        _popupOpacity = settings.PopupOpacity;
        _hideOnSameAppClick = settings.HideOnSameAppClick;
        _panelModifierKey = settings.PanelModifierKey;
        ClipboardEntry.PreviewMaxLines = settings.PreviewMaxLines;
        _quickPastes = settings.QuickPastes;

        LoadQuickPastes();
        UpdateFooterHints();

        Opacity = _popupOpacity;

        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        _hwnd = helper.Handle;

        var exStyle = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE,
            new IntPtr(exStyle.ToInt64() | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));

        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

        if (!Win32.RegisterHotKey(_hwnd, HotkeyId, _hotkeyModifiers | Win32.MOD_NOREPEAT, _hotkeyKey))
        {
            System.Windows.MessageBox.Show(
                $"热键 {settings.HotkeyDisplayName} 注册失败，可能被其他程序占用",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Win32.AddClipboardFormatListener(_hwnd);
        UpdateEmptyState();
    }

    public void Cleanup()
    {
        UninstallKeyboardHook();
        UninstallMouseHook();
        UninstallForegroundWatcher();
        Win32.UnregisterHotKey(_hwnd, HotkeyId);
        Win32.RemoveClipboardFormatListener(_hwnd);
    }

    public bool UpdateHotkey(uint modifiers, uint key)
    {
        Win32.UnregisterHotKey(_hwnd, HotkeyId);
        if (!Win32.RegisterHotKey(_hwnd, HotkeyId, modifiers | Win32.MOD_NOREPEAT, key))
        {
            Win32.RegisterHotKey(_hwnd, HotkeyId, _hotkeyModifiers | Win32.MOD_NOREPEAT, _hotkeyKey);
            return false;
        }
        _hotkeyModifiers = modifiers;
        _hotkeyKey = key;
        return true;
    }

    public void ApplySettings(AppSettings settings)
    {
        _maxItems = settings.MaxItems;
        _popupPosition = settings.PopupPosition;
        _popupOpacity = settings.PopupOpacity;
        _hideOnSameAppClick = settings.HideOnSameAppClick;
        _panelModifierKey = settings.PanelModifierKey;
        ClipboardEntry.PreviewMaxLines = settings.PreviewMaxLines;
        Opacity = _popupOpacity;
        _quickPastes = settings.QuickPastes;
        TrimItems();
        UpdateFooterHints();

        if (settings.HotkeyModifiers != _hotkeyModifiers || settings.HotkeyKey != _hotkeyKey)
        {
            if (!UpdateHotkey(settings.HotkeyModifiers, settings.HotkeyKey))
            {
                System.Windows.MessageBox.Show(
                    $"热键 {settings.HotkeyDisplayName} 注册失败，已恢复原快捷键",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                settings.HotkeyModifiers = _hotkeyModifiers;
                settings.HotkeyKey = _hotkeyKey;
            }
        }
    }

    public void ClearHistory()
    {
        _allItems.RemoveAll(x => !x.IsQuickPaste);
        RefreshFilter();
    }

    #region Quick Paste Management

    private void LoadQuickPastes()
    {
        _allItems.RemoveAll(x => x.IsQuickPaste);
        foreach (var qp in _quickPastes)
        {
            _allItems.Add(new ClipboardEntry
            {
                Type = EntryType.Text,
                TextContent = qp.Content,
                ShortcutPhrase = qp.Phrase,
                IsQuickPaste = true,
                CopiedAt = DateTime.MinValue
            });
        }
    }

    private ClipboardEntry? _phraseEditEntry;

    private void AddQuickPaste(ClipboardEntry source)
    {
        if (source.Type != EntryType.Text || string.IsNullOrEmpty(source.TextContent))
        {
            System.Windows.MessageBox.Show("仅支持文本类型设为快捷短语", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _phraseEditEntry = source;
        var preview = source.TextContent!.Length > 60
            ? source.TextContent[..60] + "…"
            : source.TextContent;
        PhrasePreview.Text = preview;
        PhraseEditBox.Text = source.ShortcutPhrase ?? "";
        PhraseEditPopup.IsOpen = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var hwndSource = PresentationSource.FromVisual(PhraseEditBox) as System.Windows.Interop.HwndSource;
            if (hwndSource != null)
            {
                Win32.SetForegroundWindow(hwndSource.Handle);
                Win32.SetFocus(hwndSource.Handle);
            }
            PhraseEditBox.Focus();
            Keyboard.Focus(PhraseEditBox);
            PhraseEditBox.SelectAll();
        });
    }

    private void CommitPhraseEdit()
    {
        var phrase = PhraseEditBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(phrase) || _phraseEditEntry == null)
        {
            PhraseEditPopup.IsOpen = false;
            _phraseEditEntry = null;
            return;
        }

        var source = _phraseEditEntry;
        _quickPastes.RemoveAll(q => q.Content == source.TextContent);
        _quickPastes.Add(new QuickPasteEntry { Phrase = phrase, Content = source.TextContent! });

        if (source.IsQuickPaste)
        {
            source.ShortcutPhrase = phrase;
        }
        else
        {
            _allItems.RemoveAll(x => x.IsQuickPaste && x.TextContent == source.TextContent);
            _allItems.Add(new ClipboardEntry
            {
                Type = EntryType.Text,
                TextContent = source.TextContent,
                ShortcutPhrase = phrase,
                IsQuickPaste = true,
                CopiedAt = DateTime.MinValue
            });
        }

        SaveQuickPastes();
        RefreshFilter();

        PhraseEditPopup.IsOpen = false;
        _phraseEditEntry = null;
    }

    private void SaveQuickPastes()
    {
        var settings = AppSettings.Load();
        settings.QuickPastes = _quickPastes;
        settings.Save();
    }

    private void PhraseConfirm_Click(object sender, MouseButtonEventArgs e) => CommitPhraseEdit();

    private void PhraseCancel_Click(object sender, MouseButtonEventArgs e)
    {
        PhraseEditPopup.IsOpen = false;
        _phraseEditEntry = null;
    }

    private void PhraseEditBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { CommitPhraseEdit(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Escape) { PhraseEditPopup.IsOpen = false; _phraseEditEntry = null; e.Handled = true; }
    }

    #endregion

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        _clickReceivedByPopup = true;
        base.OnPreviewMouseDown(e);
    }

    #region Window Message Hook

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(Win32.MA_NOACTIVATE);

            case Win32.WM_DPICHANGED:
                _isOurSetWindowPos = false;
                break;

            case Win32.WM_WINDOWPOSCHANGING:
                if (!_isOurSetWindowPos && _isPopupVisible)
                {
                    var pos = Marshal.PtrToStructure<Win32.WINDOWPOS>(lParam);
                    pos.flags |= Win32.SWP_NOMOVE;
                    Marshal.StructureToPtr(pos, lParam, false);
                }
                break;

            case Win32.WM_CLIPBOARDUPDATE:
                OnClipboardUpdate();
                handled = true;
                break;
            case Win32.WM_HOTKEY:
                if (wParam.ToInt32() == HotkeyId) { TogglePopup(); handled = true; }
                break;
        }
        return IntPtr.Zero;
    }

    #endregion

    #region Clipboard Monitoring

    private void OnClipboardUpdate()
    {
        if (_isSettingClipboard) { _isSettingClipboard = false; return; }

        try
        {
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var files = System.Windows.Clipboard.GetFileDropList();
                if (files.Count > 0)
                {
                    var paths = files.Cast<string>().ToArray();
                    DeduplicateFiles(paths);
                    _allItems.Insert(0, new ClipboardEntry { Type = EntryType.Files, FilePaths = paths });
                    TrimItems();
                    RefreshFilter();
                    return;
                }
            }

            if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    DeduplicateText(text);
                    _allItems.Insert(0, new ClipboardEntry { Type = EntryType.Text, TextContent = text });
                    TrimItems();
                    RefreshFilter();
                    return;
                }
            }

            if (System.Windows.Clipboard.ContainsImage())
            {
                var image = System.Windows.Clipboard.GetImage();
                if (image != null)
                {
                    if (image.CanFreeze) image.Freeze();
                    var pngData = ClipboardEntry.EncodeToPng(image);
                    if (pngData != null)
                    {
                        _allItems.Insert(0, new ClipboardEntry
                        {
                            Type = EntryType.Image, ImageData = pngData,
                            ImageWidth = image.PixelWidth, ImageHeight = image.PixelHeight
                        });
                        TrimItems();
                        RefreshFilter();
                    }
                }
            }
        }
        catch { }
    }

    private void DeduplicateText(string text)
    {
        _allItems.RemoveAll(x => x.Type == EntryType.Text && !x.IsQuickPaste && x.TextContent == text);
    }

    private void DeduplicateFiles(string[] paths)
    {
        var key = string.Join("|", paths);
        _allItems.RemoveAll(x => x.Type == EntryType.Files && string.Join("|", x.FilePaths ?? []) == key);
    }

    private void TrimItems()
    {
        var regular = _allItems.Where(x => !x.IsQuickPaste).ToList();
        while (regular.Count > _maxItems)
        {
            var last = regular[^1];
            _allItems.Remove(last);
            regular.RemoveAt(regular.Count - 1);
        }
    }

    #endregion

    #region Search & Filter

    private void RefreshFilter()
    {
        _displayItems.Clear();
        _firstVisibleIndex = 0;
        var query = _searchText.Trim();

        IEnumerable<ClipboardEntry> items = _allItems;

        if (_quickPhraseOnly)
            items = items.Where(i => i.IsQuickPaste);

        if (_typeFilter.HasValue)
            items = items.Where(i => i.Type == _typeFilter.Value);

        if (!string.IsNullOrEmpty(query))
            items = items.Where(i => i.SearchableText.Contains(query, StringComparison.OrdinalIgnoreCase));

        var sorted = items
            .OrderByDescending(i => i.IsQuickPaste && !string.IsNullOrEmpty(_searchText))
            .ThenByDescending(i => i.CopiedAt);

        int idx = 1;
        foreach (var item in sorted)
        {
            item.DisplayIndex = idx++;
            _displayItems.Add(item);
        }

        UpdateEmptyState();
        if (_displayItems.Count > 0)
            ItemsList.SelectedIndex = 0;
    }

    private void UpdateSearchUI()
    {
        var hasSearch = _searchText.Length > 0;
        SearchBarPanel.Visibility = hasSearch ? Visibility.Visible : Visibility.Collapsed;
        SearchTextBlock.Text = _searchText;
        SearchCountText.Text = hasSearch ? $"{_displayItems.Count} 条结果" : "";
    }

    private void UpdateEmptyState()
    {
        UpdateSearchUI();
        var hasItems = _displayItems.Count > 0;
        EmptyHint.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        ItemsList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;

        if (_searchText.Length > 0 && !hasItems)
        {
            EmptyIcon.Text = "🔍"; EmptyText.Text = "无匹配结果"; EmptySubText.Text = "尝试其他关键词";
        }
        else if (!hasItems)
        {
            EmptyIcon.Text = "📭"; EmptyText.Text = "暂无剪切板记录"; EmptySubText.Text = "复制一些文本即可开始";
        }

        var regularCount = _allItems.Count(x => !x.IsQuickPaste);
        ItemCountText.Text = regularCount > 0 ? $"({regularCount})" : "";
    }

    #endregion

    #region Popup Show/Hide

    public void TogglePopup()
    {
        if (_isPopupVisible) HidePopup();
        else ShowPopup();
    }

    private void ShowPopup()
    {
        _targetWindow = Win32.GetForegroundWindow();
        _searchText = "";
        _typeFilter = null;
        _quickPhraseOnly = false;
        TypeFilterText.Text = "全部";

        RefreshFilter();

        Left = 0;
        Top = 0;
        Opacity = 0;
        Show();
        UpdateLayout();

        PositionPopup();

        _isPopupVisible = true;

        _isOurSetWindowPos = true;
        Win32.SetWindowPos(_hwnd, IntPtr.Zero,
            _pendingPhysX, _pendingPhysY, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        _isOurSetWindowPos = false;

        Opacity = _popupOpacity;

        if (_displayItems.Count > 0)
            ItemsList.SelectedIndex = 0;

        InstallKeyboardHook();
        InstallMouseHook();
        InstallForegroundWatcher();
    }

    private void HidePopup()
    {
        _isPopupVisible = false;
        UninstallKeyboardHook();
        UninstallMouseHook();
        UninstallForegroundWatcher();
        ContextPopup.IsOpen = false;
        PhraseEditPopup.IsOpen = false;
        _phraseEditEntry = null;
        Hide();
    }

    private void PositionPopup()
    {
        const int caretGap = 24;

        if (_popupPosition == "Mouse")
        {
            Win32.GetCursorPos(out var pt);
            SetPositionWithOffset(pt.X + 8, pt.Y + 20);
            return;
        }

        var fgThread = Win32.GetWindowThreadProcessId(_targetWindow, out _);
        var gti = new Win32.GUITHREADINFO { cbSize = Marshal.SizeOf<Win32.GUITHREADINFO>() };

        if (Win32.GetGUIThreadInfo(fgThread, ref gti) && gti.hwndCaret != IntPtr.Zero)
        {
            var pt = new Win32.POINT { X = gti.rcCaret.Left, Y = gti.rcCaret.Bottom };
            Win32.ClientToScreen(gti.hwndCaret, ref pt);
            if (pt.X > 0 || pt.Y > 0)
            {
                SetPositionWithOffset(pt.X, pt.Y + caretGap);
                return;
            }
        }

        try
        {
            var myThread = Win32.GetCurrentThreadId();
            Win32.AttachThreadInput(myThread, fgThread, true);
            try
            {
                var focusWnd = Win32.GetFocus();
                if (focusWnd != IntPtr.Zero && Win32.GetCaretPos(out var caretPos))
                {
                    if (caretPos.X != 0 || caretPos.Y != 0)
                    {
                        Win32.ClientToScreen(focusWnd, ref caretPos);
                        SetPositionWithOffset(caretPos.X, caretPos.Y + caretGap);
                        return;
                    }
                }
            }
            finally { Win32.AttachThreadInput(myThread, fgThread, false); }
        }
        catch { }

        if (TryGetCaretByAutomation(out double uiaX, out double uiaY))
        {
            SetPositionWithOffset(uiaX, uiaY + caretGap);
            return;
        }

        Win32.GetCursorPos(out var cursor);
        SetPositionWithOffset(cursor.X + 8, cursor.Y + 20);
    }

    private static bool TryGetCaretByAutomation(out double x, out double y)
    {
        x = y = 0;
        try
        {
            var task = Task.Run<(bool ok, double x, double y)>(() =>
            {
                try
                {
                    var focused = System.Windows.Automation.AutomationElement.FocusedElement;
                    if (focused == null) return (false, 0, 0);

                    if (focused.TryGetCurrentPattern(
                            System.Windows.Automation.TextPattern.Pattern, out var p))
                    {
                        var sel = ((System.Windows.Automation.TextPattern)p).GetSelection();
                        if (sel.Length > 0)
                        {
                            var rects = sel[0].GetBoundingRectangles();
                            if (rects.Length > 0 && (rects[0].X > 0 || rects[0].Y > 0))
                                return (true, rects[0].X, rects[0].Bottom + 4);
                        }
                    }

                    var rect = focused.Current.BoundingRectangle;
                    if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                        return (true, rect.X + 20, rect.Bottom + 4);

                    return (false, 0, 0);
                }
                catch { return (false, 0, 0); }
            });

            if (task.Wait(200))
            {
                var result = task.Result;
                if (result.ok) { x = result.x; y = result.y; return true; }
            }
        }
        catch { }
        return false;
    }

    private void SetPositionWithOffset(double physX, double physY)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)physX, (int)physY));
        var work = screen.WorkingArea;

        var hMon = Win32.MonitorFromPoint(
            new Win32.POINT { X = (int)physX, Y = (int)physY },
            Win32.MONITOR_DEFAULTTONEAREST);
        Win32.GetDpiForMonitor(hMon, 0, out uint monDpiX, out uint monDpiY);
        double scaleX = monDpiX / 96.0;
        double scaleY = monDpiY / 96.0;

        int popupW = (int)(Width * scaleX);
        double actualH = ActualHeight > 0 ? ActualHeight : MaxHeight;
        int popupH = (int)(actualH * scaleY);

        int x = (int)physX;
        int y = (int)physY;

        if (x + popupW > work.Right) x = work.Right - popupW;
        if (y + popupH > work.Bottom) y = (int)physY - popupH - 32;
        if (x < work.Left) x = work.Left;
        if (y < work.Top) y = work.Top;

        _pendingPhysX = x;
        _pendingPhysY = y;
    }

    #endregion

    #region Keyboard Hook

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero) return;
        _hookProc = KeyboardHookCallback;
        _keyboardHook = Win32.SetWindowsHookEx(
            Win32.WH_KEYBOARD_LL, _hookProc, Win32.GetModuleHandle(null), 0);
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        _hookProc = null;
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseHookProc = MouseHookCallback;
        _mouseHook = Win32.SetWindowsHookEx(
            Win32.WH_MOUSE_LL, _mouseHookProc, Win32.GetModuleHandle(null), 0);
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        _mouseHookProc = null;
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
                    _isDragging = false;
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
                                && !ContextPopup.IsOpen && !PhraseEditPopup.IsOpen)
                                HidePopup();
                        });
                }
            }
        }
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void InstallForegroundWatcher()
    {
        if (_winEventHook != IntPtr.Zero) return;
        _winEventProc = OnForegroundChanged;
        _winEventHook = Win32.SetWinEventHook(
            Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
    }

    private void UninstallForegroundWatcher()
    {
        if (_winEventHook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        _winEventProc = null;
    }

    private void OnForegroundChanged(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (!_isPopupVisible) return;
        if (hwnd == _hwnd || hwnd == _targetWindow) return;

        Win32.GetCursorPos(out var cursor);
        if (Win32.WindowFromPoint(cursor) == _hwnd) return;

        Dispatcher.BeginInvoke(HidePopup);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isPopupVisible && wParam == (IntPtr)Win32.WM_KEYDOWN)
        {
            var kb = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);

            if (PhraseEditPopup.IsOpen)
            {
                if (kb.vkCode == Win32.VK_ESCAPE)
                {
                    Dispatcher.BeginInvoke(() => { PhraseEditPopup.IsOpen = false; _phraseEditEntry = null; });
                    return (IntPtr)1;
                }
                if (kb.vkCode == Win32.VK_RETURN)
                {
                    Dispatcher.BeginInvoke(CommitPhraseEdit);
                    return (IntPtr)1;
                }
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            if (kb.vkCode is 0x10 or 0x11 or 0x12 or 0x14
                or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5
                or 0x5B or 0x5C)
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            bool ctrlHeld = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool altHeld = (Win32.GetAsyncKeyState(0x12) & 0x8000) != 0;

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
                if (kb.vkCode == 0xBB)
                {
                    Dispatcher.BeginInvoke(() => ScrollPage(1));
                    return (IntPtr)1;
                }
                if (kb.vkCode == 0xBD)
                {
                    Dispatcher.BeginInvoke(() => ScrollPage(-1));
                    return (IntPtr)1;
                }
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            if (ctrlHeld || altHeld)
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            switch (kb.vkCode)
            {
                case Win32.VK_UP:
                    Dispatcher.BeginInvoke(() => MoveSelection(-1));
                    return (IntPtr)1;
                case Win32.VK_DOWN:
                    Dispatcher.BeginInvoke(() => MoveSelection(1));
                    return (IntPtr)1;
                case Win32.VK_LEFT:
                    Dispatcher.BeginInvoke(() => ScrollPage(-1));
                    return (IntPtr)1;
                case Win32.VK_RIGHT:
                    Dispatcher.BeginInvoke(() => ScrollPage(1));
                    return (IntPtr)1;
                case Win32.VK_RETURN:
                    Dispatcher.BeginInvoke(PasteSelectedItem);
                    return (IntPtr)1;
                case Win32.VK_ESCAPE:
                    Dispatcher.BeginInvoke(() =>
                    {
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
                case 0x09: // Tab → cycle type filter
                    Dispatcher.BeginInvoke(CycleTypeFilter);
                    return (IntPtr)1;
            }

            var ch = VkToChar(kb.vkCode, kb.scanCode);
            if (ch.HasValue)
            {
                Dispatcher.BeginInvoke(() => { _searchText += ch.Value; RefreshFilter(); });
            }

            return (IntPtr)1;
        }

        return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
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

    private void UpdateFooterHints()
    {
        string m = PanelModifierDisplayName;
        FooterHints.Inlines.Clear();
        var hintBrush = FindResource("HintText") as System.Windows.Media.Brush;

        void AddHint(string key, string label, bool dot = true)
        {
            if (dot) FooterHints.Inlines.Add(new System.Windows.Documents.Run(" · "));
            FooterHints.Inlines.Add(new System.Windows.Documents.Run(key) { Foreground = hintBrush });
            FooterHints.Inlines.Add(new System.Windows.Documents.Run(label));
        }

        AddHint($"{m}+N", "快贴", false);
        AddHint("↑↓", "选择");
        AddHint($"{m}±", "翻页");
        AddHint("Enter", "粘贴");
        AddHint($"{m}+Tab", "短语");
        AddHint("Tab", "筛选");
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

    private void MoveSelection(int delta)
    {
        if (_displayItems.Count == 0) return;
        var idx = ItemsList.SelectedIndex + delta;
        if (idx < 0) idx = 0;
        if (idx >= _displayItems.Count) idx = _displayItems.Count - 1;
        ItemsList.SelectedIndex = idx;
        ItemsList.ScrollIntoView(ItemsList.SelectedItem);
    }

    private void ScrollPage(int direction)
    {
        if (_displayItems.Count == 0) return;
        var sv = GetListScrollViewer();
        if (sv == null) return;

        double itemHeight = sv.ExtentHeight / _displayItems.Count;
        if (itemHeight <= 0) return;

        int oldFirstVisible = Math.Max(0, (int)(sv.VerticalOffset / itemHeight));
        int relSelection = Math.Max(0, ItemsList.SelectedIndex - oldFirstVisible);

        double newOffset = sv.VerticalOffset + direction * PageSize * itemHeight;
        newOffset = Math.Max(0, Math.Min(newOffset, sv.ScrollableHeight));
        sv.ScrollToVerticalOffset(newOffset);

        int newFirstVisible = Math.Max(0, (int)(newOffset / itemHeight));
        _firstVisibleIndex = newFirstVisible;
        int newSel = Math.Clamp(newFirstVisible + relSelection, 0, _displayItems.Count - 1);
        ItemsList.SelectedIndex = newSel;

        UpdateVisibleIndices(newFirstVisible);
    }

    private void UpdateVisibleIndices(int firstVisible)
    {
        for (int i = 0; i < _displayItems.Count; i++)
        {
            int rel = i - firstVisible + 1;
            _displayItems[i].DisplayIndex = (rel >= 1 && rel <= 9) ? rel : 0;
        }
    }

    private ScrollViewer? GetListScrollViewer()
    {
        if (VisualTreeHelper.GetChildrenCount(ItemsList) == 0) return null;
        var border = VisualTreeHelper.GetChild(ItemsList, 0) as System.Windows.Controls.Decorator;
        return border?.Child as ScrollViewer;
    }

    #endregion

    #region Paste

    private async void PasteSelectedItem()
    {
        if (ItemsList.SelectedItem is not ClipboardEntry item) return;

        if (!item.IsQuickPaste)
        {
            var idx = _allItems.IndexOf(item);
            if (idx > 0) { _allItems.RemoveAt(idx); _allItems.Insert(0, item); }
        }

        _isSettingClipboard = true;
        try
        {
            switch (item.Type)
            {
                case EntryType.Text:
                    System.Windows.Clipboard.SetText(item.TextContent!);
                    break;
                case EntryType.Image:
                    using (var ms = new MemoryStream(item.ImageData!))
                    {
                        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        System.Windows.Clipboard.SetImage(decoder.Frames[0]);
                    }
                    break;
                case EntryType.Files:
                    var fl = new StringCollection();
                    fl.AddRange(item.FilePaths!);
                    System.Windows.Clipboard.SetFileDropList(fl);
                    break;
            }
        }
        catch { _isSettingClipboard = false; return; }

        HidePopup();
        if (_targetWindow != IntPtr.Zero) Win32.SetForegroundWindow(_targetWindow);
        await Task.Delay(60);
        SendCtrlV();
    }

    private static void SendCtrlV()
    {
        var inputs = new Win32.INPUT[4];
        inputs[0].type = Win32.INPUT_KEYBOARD; inputs[0].u.ki.wVk = Win32.VK_CONTROL;
        inputs[1].type = Win32.INPUT_KEYBOARD; inputs[1].u.ki.wVk = Win32.VK_V;
        inputs[2].type = Win32.INPUT_KEYBOARD; inputs[2].u.ki.wVk = Win32.VK_V; inputs[2].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        inputs[3].type = Win32.INPUT_KEYBOARD; inputs[3].u.ki.wVk = Win32.VK_CONTROL; inputs[3].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        Win32.SendInput(4, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    #endregion

    #region UI Event Handlers

    private void ItemsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 || _displayItems.Count == 0) return;
        var sv = GetListScrollViewer();
        if (sv == null) return;

        double itemHeight = sv.ExtentHeight / _displayItems.Count;
        if (itemHeight <= 0) return;

        int newFirstVisible = Math.Max(0, (int)(sv.VerticalOffset / itemHeight));
        if (newFirstVisible == _firstVisibleIndex) return;

        int relSelection = Math.Max(0, ItemsList.SelectedIndex - _firstVisibleIndex);
        _firstVisibleIndex = newFirstVisible;

        int newSel = Math.Clamp(newFirstVisible + relSelection, 0, _displayItems.Count - 1);
        ItemsList.SelectedIndex = newSel;

        UpdateVisibleIndices(newFirstVisible);
    }

    private void ItemsList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not ListBoxItem)
            element = VisualTreeHelper.GetParent(element);
        if (element is ListBoxItem lbi && lbi.DataContext is ClipboardEntry)
        {
            ItemsList.SelectedItem = lbi.DataContext;
            PasteSelectedItem();
        }
    }

    private void ItemsList_PreviewMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not ListBoxItem)
            element = VisualTreeHelper.GetParent(element);
        if (element is ListBoxItem lbi && lbi.DataContext is ClipboardEntry entry)
        {
            _contextEntry = entry;
            ItemsList.SelectedItem = entry;
            CtxShortcutText.Text = entry.IsQuickPaste ? "⚡ 修改快捷短语" : "⚡ 设为快捷短语";
            ContextPopup.IsOpen = true;
            e.Handled = true;
        }
    }

    private void CtxPaste_Click(object sender, MouseButtonEventArgs e)
    {
        ContextPopup.IsOpen = false;
        if (_contextEntry != null) { ItemsList.SelectedItem = _contextEntry; PasteSelectedItem(); }
    }

    private void CtxShortcut_Click(object sender, MouseButtonEventArgs e)
    {
        ContextPopup.IsOpen = false;
        if (_contextEntry != null) AddQuickPaste(_contextEntry);
    }

    private void CtxDelete_Click(object sender, MouseButtonEventArgs e)
    {
        ContextPopup.IsOpen = false;
        if (_contextEntry == null) return;

        if (_contextEntry.IsQuickPaste)
            _quickPastes.RemoveAll(q => q.Content == _contextEntry.TextContent);

        _allItems.Remove(_contextEntry);
        RefreshFilter();

        if (_contextEntry.IsQuickPaste) SaveQuickPastes();
    }

    private void TypeFilter_Click(object sender, MouseButtonEventArgs e) => CycleTypeFilter();

    private void CycleTypeFilter()
    {
        _quickPhraseOnly = false;
        _typeFilter = _typeFilter switch
        {
            null => EntryType.Text,
            EntryType.Text => EntryType.Image,
            EntryType.Image => EntryType.Files,
            _ => null
        };
        TypeFilterText.Text = _typeFilter switch
        {
            EntryType.Text => "📝 文本",
            EntryType.Image => "🖼️ 图片",
            EntryType.Files => "📁 文件",
            _ => "全部"
        };
        RefreshFilter();
    }

    private void ToggleQuickPhraseFilter()
    {
        _quickPhraseOnly = !_quickPhraseOnly;
        if (_quickPhraseOnly)
        {
            _typeFilter = null;
            TypeFilterText.Text = "⚡ 短语";
        }
        else
        {
            TypeFilterText.Text = "全部";
        }
        RefreshFilter();
    }

    private void PasteByIndex(int index)
    {
        var item = _displayItems.FirstOrDefault(i => i.DisplayIndex == index);
        if (item == null) return;
        ItemsList.SelectedItem = item;
        PasteSelectedItem();
    }

    private void Settings_Click(object sender, MouseButtonEventArgs e)
    {
        HidePopup();
        SettingsRequested?.Invoke();
    }

    private void Header_DragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _isDragging = true;
        Win32.GetCursorPos(out _dragLastPt);
    }

    #endregion
}
