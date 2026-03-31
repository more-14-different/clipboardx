using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ClipboardManager;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string _originalTheme;
    private uint _pendingModifiers;
    private uint _pendingKey;
    private bool _isRecordingHotkey;
    private string _pendingTheme;
    private string _pendingPosition;
    private double _pendingOpacity;
    private bool _pendingHideOnClick;
    private string _pendingModifierKey;

    private static readonly string[] ModifierOptions = ["Ctrl", "Alt", "Win", "CapsLock"];

    public event Action? ClearHistoryRequested;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _originalTheme = settings.Theme;

        MaxItemsBox.Text = settings.MaxItems.ToString();
        _pendingModifiers = settings.HotkeyModifiers;
        _pendingKey = settings.HotkeyKey;
        HotkeyText.Text = settings.HotkeyDisplayName;

        _pendingTheme = settings.Theme;
        ThemeText.Text = ThemeDisplayName(_pendingTheme);

        _pendingPosition = settings.PopupPosition;
        PositionText.Text = PositionDisplayName(_pendingPosition);

        _pendingOpacity = settings.PopupOpacity;
        OpacitySlider.Value = _pendingOpacity;
        OpacityValueText.Text = $"{(int)(_pendingOpacity * 100)}%";

        _pendingHideOnClick = settings.HideOnSameAppClick;
        ClickHideText.Text = _pendingHideOnClick ? "任意点击隐藏" : "仅切换应用隐藏";

        PreviewLinesBox.Text = settings.PreviewMaxLines.ToString();

        _pendingModifierKey = settings.PanelModifierKey;
        ModifierText.Text = ModifierDisplayName(_pendingModifierKey);
    }

    private static string ThemeDisplayName(string t) => t switch
    {
        "Dark" => "暗色",
        "Light" => "亮色",
        _ => "跟随系统"
    };

    private static string PositionDisplayName(string p) => p switch
    {
        "Mouse" => "鼠标处",
        _ => "光标处"
    };

    private void HotkeyBox_Click(object sender, RoutedEventArgs e)
    {
        _isRecordingHotkey = true;
        HotkeyText.Text = "按下快捷键…";
        HotkeyText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBg");
        HotkeyBox.Focus();
    }

    private void HotkeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecordingHotkey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None) return;

        uint mod = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) mod |= Win32.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mod |= Win32.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mod |= Win32.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mod |= Win32.MOD_WIN;

        _pendingModifiers = mod;
        _pendingKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        _isRecordingHotkey = false;

        var tmp = new AppSettings { HotkeyModifiers = _pendingModifiers, HotkeyKey = _pendingKey };
        HotkeyText.Text = tmp.HotkeyDisplayName;
        HotkeyText.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText");
    }

    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isRecordingHotkey)
        {
            _isRecordingHotkey = false;
            var tmp = new AppSettings { HotkeyModifiers = _pendingModifiers, HotkeyKey = _pendingKey };
            HotkeyText.Text = tmp.HotkeyDisplayName;
            HotkeyText.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText");
        }
    }

    private void ThemeCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingTheme = _pendingTheme switch
        {
            "System" => "Dark",
            "Dark" => "Light",
            _ => "System"
        };
        ThemeText.Text = ThemeDisplayName(_pendingTheme);
        ThemeManager.Apply(_pendingTheme);
    }

    private void PositionCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingPosition = _pendingPosition == "Caret" ? "Mouse" : "Caret";
        PositionText.Text = PositionDisplayName(_pendingPosition);
    }

    private void ClickHideCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingHideOnClick = !_pendingHideOnClick;
        ClickHideText.Text = _pendingHideOnClick ? "任意点击隐藏" : "仅切换应用隐藏";
    }

    private static string ModifierDisplayName(string m) => m switch
    {
        "Alt" => "Alt",
        "Win" => "Win",
        "CapsLock" => "CapsLock",
        _ => "Ctrl",
    };

    private void ModifierCycle_Click(object sender, RoutedEventArgs e)
    {
        int idx = Array.IndexOf(ModifierOptions, _pendingModifierKey);
        _pendingModifierKey = ModifierOptions[(idx + 1) % ModifierOptions.Length];
        ModifierText.Text = ModifierDisplayName(_pendingModifierKey);
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValueText == null) return;
        _pendingOpacity = Math.Round(e.NewValue, 2);
        OpacityValueText.Text = $"{(int)(_pendingOpacity * 100)}%";
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show("确定要清空所有剪切板历史？\n（快捷短语不受影响）",
                "确认清空", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            ClearHistoryRequested?.Invoke();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MaxItemsBox.Text, out var maxItems) || maxItems < 10 || maxItems > 100000)
        {
            System.Windows.MessageBox.Show("最大记录数应在 10 ~ 100000 之间", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(PreviewLinesBox.Text, out var previewLines) || previewLines < 1 || previewLines > 10)
        {
            System.Windows.MessageBox.Show("预览行数应在 1 ~ 10 之间", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.MaxItems = maxItems;
        _settings.HotkeyModifiers = _pendingModifiers;
        _settings.HotkeyKey = _pendingKey;
        _settings.Theme = _pendingTheme;
        _settings.PopupPosition = _pendingPosition;
        _settings.PopupOpacity = _pendingOpacity;
        _settings.HideOnSameAppClick = _pendingHideOnClick;
        _settings.PreviewMaxLines = previewLines;
        _settings.PanelModifierKey = _pendingModifierKey;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Apply(_originalTheme);
        DialogResult = false;
        Close();
    }
}
