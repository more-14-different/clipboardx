using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ClipboardManager;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string _originalTheme;
    private uint _pendingModifiers;
    private uint _pendingKey;
    private bool _isRecordingHotkey;
    private uint _pendingFileJumpModifiers;
    private uint _pendingFileJumpKey;
    private bool _isRecordingFileJumpHotkey;
    private uint _pendingBatchModeCycleModifiers;
    private uint _pendingBatchModeCycleKey;
    private bool _isRecordingBatchModeCycleHotkey;
    private string _pendingTheme;
    private string _pendingPosition;
    private double _pendingOpacity;
    private bool _pendingHideOnClick;
    private bool _pendingRunAtStartup;
    private bool _pendingRunAsAdministrator;
    private bool _pendingCheckUpdatesOnStartup;
    private bool _pendingEnableShellNavigateInject;
    private string _pendingFileJumpFollowMode = FileJumpPickerFollowModes.Dialog;
    private bool _pendingFileJumpAutoPopup = true;
    private bool _pendingFileJumpOpenWhenDialogForeground = true;
    private bool _pendingFileJumpAutoOnFirstClick;
    private bool _pendingFileJumpAutoSyncOnReturn;
    private string _pendingModifierKey;
    private bool _pendingBatchPasteMergeText;
    private bool _pendingBatchQueueAutoSwitchToNormalAfterQueueDone;
    private uint _pendingPageScrollUpModifiers;
    private uint _pendingPageScrollUpKey;
    private uint _pendingPageScrollDownModifiers;
    private uint _pendingPageScrollDownKey;
    private bool _isRecordingPageScrollUpHotkey;
    private bool _isRecordingPageScrollDownHotkey;
    private string _pendingPasteSimulationMode = PasteSimulationModes.CtrlV;

    private static readonly string[] ModifierOptions = ["Ctrl", "Alt", "Win", "CapsLock"];

    public event Action? ClearHistoryRequested;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Icon = App.GetWindowIconSource();
        _settings = settings;
        _originalTheme = settings.Theme;

#if !CLIPX_CLIPBOARD
        ClipboardTab.Visibility = Visibility.Collapsed;
#endif
#if !CLIPX_FILEJUMP
        FileJumpTab.Visibility = Visibility.Collapsed;
        CustomDialogTab.Visibility = Visibility.Collapsed;
#endif

        MaxItemsBox.Text = settings.MaxItems.ToString();
        _pendingModifiers = settings.HotkeyModifiers;
        _pendingKey = settings.HotkeyKey;
        HotkeyText.Text = settings.HotkeyDisplayName;

        _pendingFileJumpModifiers = settings.FileJumpHotkeyModifiers;
        _pendingFileJumpKey = settings.FileJumpHotkeyKey;
        FileJumpHotkeyText.Text = settings.FileJumpHotkeyDisplayName;

        _pendingBatchModeCycleModifiers = settings.BatchModeCycleHotkeyModifiers;
        _pendingBatchModeCycleKey = settings.BatchModeCycleHotkeyKey;
        BatchModeCycleHotkeyText.Text = settings.BatchModeCycleHotkeyDisplayName;
        FileJumpDelayMsBox.Text = settings.FileJumpPickerShowDelayMs.ToString();

        _pendingTheme = settings.Theme;
        ThemeText.Text = ThemeDisplayName(_pendingTheme);

        _pendingPosition = settings.PopupPosition;
        PositionText.Text = PositionDisplayName(_pendingPosition);

        _pendingOpacity = settings.PopupOpacity;
        OpacitySlider.Value = _pendingOpacity;
        OpacityValueText.Text = $"{(int)(_pendingOpacity * 100)}%";

        _pendingHideOnClick = settings.HideOnSameAppClick;
        ClickHideText.Text = _pendingHideOnClick ? "任意点击隐藏" : "仅切换应用隐藏";

        _pendingRunAtStartup = settings.RunAtStartup;
        StartupText.Text = _pendingRunAtStartup ? "开启" : "关闭";

        _pendingRunAsAdministrator = settings.RunAsAdministrator;
        RunAsAdminText.Text = _pendingRunAsAdministrator ? "开启" : "关闭";

        _pendingCheckUpdatesOnStartup = settings.CheckUpdatesOnStartup;
        CheckUpdateOnStartupText.Text = _pendingCheckUpdatesOnStartup ? "开启" : "关闭";

        _pendingEnableShellNavigateInject = settings.EnableShellNavigateInject;
        ShellInjectText.Text = _pendingEnableShellNavigateInject ? "开启" : "关闭";

        _pendingFileJumpFollowMode = FileJumpPickerFollowModes.Normalize(settings.FileJumpPickerFollowMode);
        FileJumpFollowText.Text = FileJumpPickerFollowModes.IsDialog(_pendingFileJumpFollowMode) ? "跟随对话框" : "跟随鼠标";

        _pendingFileJumpAutoPopup = settings.FileJumpPickerAutoPopup;
        FileJumpAutoPopupText.Text = _pendingFileJumpAutoPopup ? "开启" : "关闭";

        _pendingFileJumpOpenWhenDialogForeground = settings.FileJumpPickerOpenWhenDialogForeground;
        FileJumpOpenOnForegroundText.Text = _pendingFileJumpOpenWhenDialogForeground ? "开启" : "关闭";

        _pendingFileJumpAutoOnFirstClick = settings.FileJumpAutoOnFirstClick;
        FileJumpAutoClickText.Text = _pendingFileJumpAutoOnFirstClick ? "开启" : "关闭";

        _pendingFileJumpAutoSyncOnReturn = settings.FileJumpAutoSyncOnReturn;
        FileJumpAutoSyncText.Text = _pendingFileJumpAutoSyncOnReturn ? "开启" : "关闭";

        PreviewLinesBox.Text = settings.PreviewMaxLines.ToString();

        PopupWidthBox.Text = settings.PopupPanelWidth.ToString("0");
        PopupMaxHeightBox.Text = settings.PopupPanelMaxHeight.ToString("0");
        PopupPageItemsBox.Text = settings.PopupPageItems.ToString();
        _pendingPageScrollUpModifiers = settings.PanelPageScrollUpModifiers;
        _pendingPageScrollUpKey = settings.PanelPageScrollUpKey;
        _pendingPageScrollDownModifiers = settings.PanelPageScrollDownModifiers;
        _pendingPageScrollDownKey = settings.PanelPageScrollDownKey;
        PanelPageUpKeyText.Text = AppSettings.FormatHotkey(_pendingPageScrollUpModifiers, _pendingPageScrollUpKey);
        PanelPageDownKeyText.Text = AppSettings.FormatHotkey(_pendingPageScrollDownModifiers, _pendingPageScrollDownKey);

        _pendingModifierKey = settings.PanelModifierKey;
        ModifierText.Text = ModifierDisplayName(_pendingModifierKey);

        _pendingPasteSimulationMode = PasteSimulationModes.Normalize(settings.PasteSimulationMode);
        PasteSimulationText.Text = PasteSimulationDisplayName(_pendingPasteSimulationMode);

        _pendingBatchPasteMergeText = settings.BatchPasteMergeText;
        BatchPasteMergeToggleText.Text = _pendingBatchPasteMergeText ? "开启" : "关闭";

        _pendingBatchQueueAutoSwitchToNormalAfterQueueDone = settings.BatchQueueAutoSwitchToNormalAfterQueueDone;
        BatchQueueAutoNormalToggleText.Text = _pendingBatchQueueAutoSwitchToNormalAfterQueueDone ? "开启" : "关闭";

        CustomFileDialogStore.RulesChanged += OnCustomFileDialogRulesChanged;
        Closed += SettingsWindow_OnClosed;
        Loaded += SettingsWindow_OnLoaded;
        ReloadCustomFileDialogList();
        CustomRulesPathHint.Text = "存储文件：" + CustomFileDialogStore.PersistencePath;
    }

    private void SettingsWindow_OnClosed(object? sender, EventArgs e)
    {
        CustomFileDialogStore.RulesChanged -= OnCustomFileDialogRulesChanged;
    }

    private void SettingsWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= SettingsWindow_OnLoaded;
        // 托盘/置顶浮层场景下模态窗易落在 Z 序底层；延后到 Input 再夺前台。
        Dispatcher.BeginInvoke(() =>
        {
            Activate();
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                if (h != IntPtr.Zero)
                    Win32.SetForegroundWindowAggressive(h);
            }
            catch { /* ignore */ }
        }, DispatcherPriority.Input);
    }

    private void OnCustomFileDialogRulesChanged()
    {
        if (Dispatcher.CheckAccess())
            ReloadCustomFileDialogList();
        else
            Dispatcher.Invoke(ReloadCustomFileDialogList);
    }

    private void ReloadCustomFileDialogList()
    {
        CustomRulesList.Items.Clear();
        foreach (var r in CustomFileDialogStore.GetRules())
            CustomRulesList.Items.Add(r);
    }

    private void CustomRuleDelete_Click(object sender, RoutedEventArgs e)
    {
        if (CustomRulesList.SelectedItem is not CustomFileDialogRule r)
            return;

        if (System.Windows.MessageBox.Show(
                $"删除此规则？\n{r.SummaryLine}",
                "确认",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        CustomFileDialogStore.RemoveRule(r.Id);
        ReloadCustomFileDialogList();
    }

    private void CustomRuleWizard_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
            app.StartCustomFileDialogWizard();
    }

    private void CustomRuleImportMerge_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON (*.json)|*.json|所有文件 (*.*)|*.*",
            Title = "导入规则（与本地合并）",
        };
        if (dlg.ShowDialog(this) != true)
            return;

        var n = CustomFileDialogStore.ImportMergeFromFile(dlg.FileName, out var err);
        if (err != null)
        {
            System.Windows.MessageBox.Show("导入失败：\n" + err, "自定义文件对话框",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        System.Windows.MessageBox.Show(
            n > 0 ? $"已合并写入 {n} 条有效规则。" : "文件中没有可导入的有效规则（需包含非空的 windowClass）。",
            "自定义文件对话框",
            MessageBoxButton.OK,
            n > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void CustomRuleImportReplace_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON (*.json)|*.json|所有文件 (*.*)|*.*",
            Title = "导入规则（完全替换）",
        };
        if (dlg.ShowDialog(this) != true)
            return;

        if (System.Windows.MessageBox.Show(
                "将删除当前所有自定义规则，并替换为文件中的列表。\n确定继续？",
                "确认替换",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var n = CustomFileDialogStore.ImportReplaceFromFile(dlg.FileName, out var err);
        if (err != null)
        {
            System.Windows.MessageBox.Show("导入失败：\n" + err, "自定义文件对话框",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        System.Windows.MessageBox.Show(
            $"已替换为 {n} 条规则。",
            "自定义文件对话框",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void CustomRuleExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = "clipboardx_custom_file_dialogs.json",
            Title = "导出自定义文件对话框规则",
        };
        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            CustomFileDialogStore.ExportToFile(dlg.FileName);
            System.Windows.MessageBox.Show("导出完成。", "自定义文件对话框",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("导出失败：\n" + ex.Message, "自定义文件对话框",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
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

    /// <summary>
    /// 录制全局快捷键时的修饰键掩码。WPF 的 <see cref="Keyboard.Modifiers"/> 在按住 Win 时常为 None，
    /// 需用 <see cref="Win32.GetAsyncKeyState"/> 检测左右 Win。
    /// </summary>
    private static uint GetHotkeyModifiersForRecording()
    {
        var wpf = Keyboard.Modifiers;
        uint mod = 0;
        if (wpf.HasFlag(ModifierKeys.Control)) mod |= Win32.MOD_CONTROL;
        if (wpf.HasFlag(ModifierKeys.Shift)) mod |= Win32.MOD_SHIFT;
        if (wpf.HasFlag(ModifierKeys.Alt)) mod |= Win32.MOD_ALT;
        if (wpf.HasFlag(ModifierKeys.Windows)) mod |= Win32.MOD_WIN;
        if ((Win32.GetAsyncKeyState(Win32.VK_LWIN) & 0x8000) != 0
            || (Win32.GetAsyncKeyState(Win32.VK_RWIN) & 0x8000) != 0)
            mod |= Win32.MOD_WIN;
        return mod;
    }

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

        uint mod = GetHotkeyModifiersForRecording();
        if (mod == 0) return;

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

    private void FileJumpHotkeyBox_Click(object sender, RoutedEventArgs e)
    {
        _isRecordingFileJumpHotkey = true;
        FileJumpHotkeyText.Text = "按下快捷键…";
        FileJumpHotkeyText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBg");
        FileJumpHotkeyBox.Focus();
    }

    private void FileJumpHotkeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecordingFileJumpHotkey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        uint mod = GetHotkeyModifiersForRecording();
        if (mod == 0) return;

        _pendingFileJumpModifiers = mod;
        _pendingFileJumpKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        _isRecordingFileJumpHotkey = false;

        FileJumpHotkeyText.Text = AppSettings.FormatHotkey(_pendingFileJumpModifiers, _pendingFileJumpKey);
        FileJumpHotkeyText.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText");
    }

    private void FileJumpHotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isRecordingFileJumpHotkey)
        {
            _isRecordingFileJumpHotkey = false;
            FileJumpHotkeyText.Text = AppSettings.FormatHotkey(_pendingFileJumpModifiers, _pendingFileJumpKey);
            FileJumpHotkeyText.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText");
        }
    }

    private void BatchModeCycleHotkeyBox_Click(object sender, RoutedEventArgs e)
    {
        _isRecordingBatchModeCycleHotkey = true;
        BatchModeCycleHotkeyText.Text = "按下快捷键…";
        BatchModeCycleHotkeyText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBg");
        BatchModeCycleHotkeyBox.Focus();
    }

    private void BatchModeCycleHotkeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecordingBatchModeCycleHotkey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        uint mod = GetHotkeyModifiersForRecording();
        if (mod == 0) return;

        _pendingBatchModeCycleModifiers = mod;
        _pendingBatchModeCycleKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        _isRecordingBatchModeCycleHotkey = false;

        BatchModeCycleHotkeyText.Text = AppSettings.FormatHotkey(_pendingBatchModeCycleModifiers, _pendingBatchModeCycleKey);
        BatchModeCycleHotkeyText.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText");
    }

    private void BatchModeCycleHotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isRecordingBatchModeCycleHotkey)
        {
            _isRecordingBatchModeCycleHotkey = false;
            BatchModeCycleHotkeyText.Text = AppSettings.FormatHotkey(_pendingBatchModeCycleModifiers, _pendingBatchModeCycleKey);
            BatchModeCycleHotkeyText.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText");
        }
    }

    private void PanelPageUpKeyBox_Click(object sender, RoutedEventArgs e)
    {
        _isRecordingPageScrollUpHotkey = true;
        PanelPageUpKeyText.Text = "按下组合键…";
        PanelPageUpKeyText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBg");
        PanelPageUpKeyBox.Focus();
    }

    private void PanelPageUpKeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecordingPageScrollUpHotkey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        uint mod = GetHotkeyModifiersForRecording();
        if (mod == 0) return;

        _pendingPageScrollUpModifiers = mod;
        _pendingPageScrollUpKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        _isRecordingPageScrollUpHotkey = false;
        PanelPageUpKeyText.Text = AppSettings.FormatHotkey(_pendingPageScrollUpModifiers, _pendingPageScrollUpKey);
        PanelPageUpKeyText.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText");
    }

    private void PanelPageUpKeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isRecordingPageScrollUpHotkey)
        {
            _isRecordingPageScrollUpHotkey = false;
            PanelPageUpKeyText.Text = AppSettings.FormatHotkey(_pendingPageScrollUpModifiers, _pendingPageScrollUpKey);
            PanelPageUpKeyText.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText");
        }
    }

    private void PanelPageDownKeyBox_Click(object sender, RoutedEventArgs e)
    {
        _isRecordingPageScrollDownHotkey = true;
        PanelPageDownKeyText.Text = "按下组合键…";
        PanelPageDownKeyText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBg");
        PanelPageDownKeyBox.Focus();
    }

    private void PanelPageDownKeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecordingPageScrollDownHotkey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        uint mod = GetHotkeyModifiersForRecording();
        if (mod == 0) return;

        _pendingPageScrollDownModifiers = mod;
        _pendingPageScrollDownKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        _isRecordingPageScrollDownHotkey = false;
        PanelPageDownKeyText.Text = AppSettings.FormatHotkey(_pendingPageScrollDownModifiers, _pendingPageScrollDownKey);
        PanelPageDownKeyText.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText");
    }

    private void PanelPageDownKeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isRecordingPageScrollDownHotkey)
        {
            _isRecordingPageScrollDownHotkey = false;
            PanelPageDownKeyText.Text = AppSettings.FormatHotkey(_pendingPageScrollDownModifiers, _pendingPageScrollDownKey);
            PanelPageDownKeyText.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText");
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

    private void StartupCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingRunAtStartup = !_pendingRunAtStartup;
        StartupText.Text = _pendingRunAtStartup ? "开启" : "关闭";
    }

    private void RunAsAdminCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingRunAsAdministrator = !_pendingRunAsAdministrator;
        RunAsAdminText.Text = _pendingRunAsAdministrator ? "开启" : "关闭";
    }

    private void CheckUpdateOnStartupCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingCheckUpdatesOnStartup = !_pendingCheckUpdatesOnStartup;
        CheckUpdateOnStartupText.Text = _pendingCheckUpdatesOnStartup ? "开启" : "关闭";
    }

    private void ShellInjectCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingEnableShellNavigateInject = !_pendingEnableShellNavigateInject;
        ShellInjectText.Text = _pendingEnableShellNavigateInject ? "开启" : "关闭";
    }

    private void FileJumpFollowCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingFileJumpFollowMode = FileJumpPickerFollowModes.IsDialog(_pendingFileJumpFollowMode)
            ? FileJumpPickerFollowModes.Mouse
            : FileJumpPickerFollowModes.Dialog;
        FileJumpFollowText.Text = FileJumpPickerFollowModes.IsDialog(_pendingFileJumpFollowMode) ? "跟随对话框" : "跟随鼠标";
    }

    private void FileJumpAutoPopupCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingFileJumpAutoPopup = !_pendingFileJumpAutoPopup;
        FileJumpAutoPopupText.Text = _pendingFileJumpAutoPopup ? "开启" : "关闭";
    }

    private void FileJumpOpenOnForegroundCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingFileJumpOpenWhenDialogForeground = !_pendingFileJumpOpenWhenDialogForeground;
        FileJumpOpenOnForegroundText.Text = _pendingFileJumpOpenWhenDialogForeground ? "开启" : "关闭";
    }

    private void FileJumpAutoClickCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingFileJumpAutoOnFirstClick = !_pendingFileJumpAutoOnFirstClick;
        FileJumpAutoClickText.Text = _pendingFileJumpAutoOnFirstClick ? "开启" : "关闭";
    }

    private void FileJumpAutoSyncCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingFileJumpAutoSyncOnReturn = !_pendingFileJumpAutoSyncOnReturn;
        FileJumpAutoSyncText.Text = _pendingFileJumpAutoSyncOnReturn ? "开启" : "关闭";
    }

    private static string ModifierDisplayName(string m) => m switch
    {
        "Alt" => "Alt",
        "Win" => "Win",
        "CapsLock" => "CapsLock",
        _ => "Ctrl",
    };

    private static string PasteSimulationDisplayName(string m) =>
        PasteSimulationModes.Normalize(m) == PasteSimulationModes.ShiftInsert ? "Shift+Insert" : "Ctrl+V";

    private void PasteSimulationCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingPasteSimulationMode = _pendingPasteSimulationMode == PasteSimulationModes.CtrlV
            ? PasteSimulationModes.ShiftInsert
            : PasteSimulationModes.CtrlV;
        PasteSimulationText.Text = PasteSimulationDisplayName(_pendingPasteSimulationMode);
    }

    private void ModifierCycle_Click(object sender, RoutedEventArgs e)
    {
        int idx = Array.IndexOf(ModifierOptions, _pendingModifierKey);
        _pendingModifierKey = ModifierOptions[(idx + 1) % ModifierOptions.Length];
        ModifierText.Text = ModifierDisplayName(_pendingModifierKey);
    }

    private void BatchPasteMergeCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingBatchPasteMergeText = !_pendingBatchPasteMergeText;
        BatchPasteMergeToggleText.Text = _pendingBatchPasteMergeText ? "开启" : "关闭";
    }

    private void BatchQueueAutoNormalCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingBatchQueueAutoSwitchToNormalAfterQueueDone = !_pendingBatchQueueAutoSwitchToNormalAfterQueueDone;
        BatchQueueAutoNormalToggleText.Text = _pendingBatchQueueAutoSwitchToNormalAfterQueueDone ? "开启" : "关闭";
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

        if (!double.TryParse(PopupWidthBox.Text, out var popupW) || popupW < 280 || popupW > 1200)
        {
            System.Windows.MessageBox.Show("面板宽度应在 280 ~ 1200（像素）之间", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!double.TryParse(PopupMaxHeightBox.Text, out var popupH) || popupH < 200 || popupH > 900)
        {
            System.Windows.MessageBox.Show("面板最大高度应在 200 ~ 900（像素）之间", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(PopupPageItemsBox.Text, out var pageItems) || pageItems < 1 || pageItems > 50)
        {
            System.Windows.MessageBox.Show("每次翻页条数应在 1 ~ 50 之间", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_pendingPageScrollUpModifiers == _pendingPageScrollDownModifiers
            && _pendingPageScrollUpKey == _pendingPageScrollDownKey)
        {
            System.Windows.MessageBox.Show("向上翻页与向下翻页快捷键不能相同。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(FileJumpDelayMsBox.Text, out var jumpDelayMs) || jumpDelayMs < 0 || jumpDelayMs > 10000)
        {
            System.Windows.MessageBox.Show("跳转列表延时应在 0 ~ 10000 毫秒之间（0 表示立即弹出）", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_pendingModifiers == _pendingFileJumpModifiers && _pendingKey == _pendingFileJumpKey)
        {
            System.Windows.MessageBox.Show("呼出快捷键与文件对话框跳转键不能相同。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_pendingModifiers == _pendingBatchModeCycleModifiers && _pendingKey == _pendingBatchModeCycleKey)
        {
            System.Windows.MessageBox.Show("呼出快捷键与批量模式切换键不能相同。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_pendingFileJumpModifiers == _pendingBatchModeCycleModifiers && _pendingFileJumpKey == _pendingBatchModeCycleKey)
        {
            System.Windows.MessageBox.Show("文件对话框跳转键与批量模式切换键不能相同。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.MaxItems = maxItems;
        _settings.HotkeyModifiers = _pendingModifiers;
        _settings.HotkeyKey = _pendingKey;
        _settings.FileJumpHotkeyModifiers = _pendingFileJumpModifiers;
        _settings.FileJumpHotkeyKey = _pendingFileJumpKey;
        _settings.BatchModeCycleHotkeyModifiers = _pendingBatchModeCycleModifiers;
        _settings.BatchModeCycleHotkeyKey = _pendingBatchModeCycleKey;
        _settings.FileJumpPickerShowDelayMs = jumpDelayMs;
        _settings.Theme = _pendingTheme;
        _settings.PopupPosition = _pendingPosition;
        _settings.PopupOpacity = _pendingOpacity;
        _settings.HideOnSameAppClick = _pendingHideOnClick;
        _settings.RunAtStartup = _pendingRunAtStartup;
        _settings.RunAsAdministrator = _pendingRunAsAdministrator;
        _settings.CheckUpdatesOnStartup = _pendingCheckUpdatesOnStartup;
        _settings.EnableShellNavigateInject = _pendingEnableShellNavigateInject;
        _settings.FileJumpPickerFollowMode = FileJumpPickerFollowModes.Normalize(_pendingFileJumpFollowMode);
        _settings.FileJumpPickerAutoPopup = _pendingFileJumpAutoPopup;
        _settings.FileJumpPickerOpenWhenDialogForeground = _pendingFileJumpOpenWhenDialogForeground;
        _settings.FileJumpAutoOnFirstClick = _pendingFileJumpAutoOnFirstClick;
        _settings.FileJumpAutoSyncOnReturn = _pendingFileJumpAutoSyncOnReturn;
        _settings.PreviewMaxLines = previewLines;
        _settings.PopupPanelWidth = popupW;
        _settings.PopupPanelMaxHeight = popupH;
        _settings.PopupPageItems = pageItems;
        _settings.PanelPageScrollUpModifiers = _pendingPageScrollUpModifiers;
        _settings.PanelPageScrollUpKey = _pendingPageScrollUpKey;
        _settings.PanelPageScrollDownModifiers = _pendingPageScrollDownModifiers;
        _settings.PanelPageScrollDownKey = _pendingPageScrollDownKey;
        AppSettings.NormalizePopupPanelSettings(_settings);
        _settings.PanelModifierKey = _pendingModifierKey;
        _settings.PasteSimulationMode = PasteSimulationModes.Normalize(_pendingPasteSimulationMode);
        _settings.BatchPasteMergeText = _pendingBatchPasteMergeText;
        _settings.BatchQueueAutoSwitchToNormalAfterQueueDone = _pendingBatchQueueAutoSwitchToNormalAfterQueueDone;

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
