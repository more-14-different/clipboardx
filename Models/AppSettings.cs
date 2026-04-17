using System.IO;
using System.Text.Json;

namespace ClipboardManager;

public class QuickPasteEntry
{
    public string Phrase { get; set; } = "";
    public string Content { get; set; } = "";
}

public class AppSettings
{
    public int MaxItems { get; set; } = 2000;
    public uint HotkeyModifiers { get; set; } = Win32.MOD_CONTROL;
    public uint HotkeyKey { get; set; } = Win32.VK_OEM_3;

    /// <summary>「打开/保存」对话框中跳转到文件夹的全局快捷键（默认 Ctrl+G）。</summary>
    public uint FileJumpHotkeyModifiers { get; set; } = Win32.MOD_CONTROL;
    public uint FileJumpHotkeyKey { get; set; } = Win32.VK_G;

    /// <summary>多候选时跳转列表弹出前的延时（毫秒）；0 表示立即弹出。</summary>
    public int FileJumpPickerShowDelayMs { get; set; } = 100;

    /// <summary>Mouse：跳转列表跟随鼠标附近；Dialog：紧贴文件对话框并随窗口移动。</summary>
    public string FileJumpPickerFollowMode { get; set; } = FileJumpPickerFollowModes.Dialog;

    /// <summary>多候选时是否弹出跳转列表（关则直跳最优路径）；快捷键与 FileJumpPickerOpenWhenDialogForeground 共用。</summary>
    public bool FileJumpPickerAutoPopup { get; set; } = true;

    /// <summary>
    /// 检测到文件对话框到前台时是否自动执行跳转逻辑（无需快捷键）；具体弹列表还是直跳由 FileJumpPickerAutoPopup 决定。
    /// 开启时跳转列表 UI 始终贴靠文件对话框（与 FileJumpPickerFollowMode 的「跟随鼠标」无关）；快捷键打开的列表仍遵跟随模式。
    /// </summary>
    public bool FileJumpPickerOpenWhenDialogForeground { get; set; } = true;

    /// <summary>
    /// 从资源管理器/TC 切回已打开的文件对话框时，重新采集候选列表；
    /// 若最新外部文件夹与对话框当前路径不同，则自动跳转（可关闭）。
    /// 与 <see cref="FileJumpPickerOpenWhenDialogForeground"/> 的区别：后者只在「首次」到前台时触发一次，
    /// 此选项在「每次」切回时重新采集并比较。
    /// </summary>
    public bool FileJumpAutoSyncOnReturn { get; set; } = true;

    /// <summary>
    /// 系统公共文件对话框内跳转时，是否尝试将 Shell 导航 DLL 注入宿主进程（IShellBrowser::BrowseObject）。
    /// 关闭后仅走地址栏/键入模拟，兼容部分杀软或宿主拦截注入的环境；WPS 等自定义对话框始终不注入。
    /// </summary>
    public bool EnableShellNavigateInject { get; set; } = true;

    /// <summary>
    /// 打开/保存对话框成为前台后，第一次在该对话框内按下鼠标左键时，自动跳转到候选路径列表的第一条（与 Ctrl+G 列表顺序一致）；
    /// 每个对话框顶层窗口存活期内仅成功自动跳转一次。
    /// </summary>
    public bool FileJumpAutoOnFirstClick { get; set; } = true;

    public string Theme { get; set; } = "System";
    public string PopupPosition { get; set; } = "Caret";
    public double PopupOpacity { get; set; } = 1.0;
    public bool HideOnSameAppClick { get; set; } = true;
    /// <summary>登录 Windows 时自动启动本程序（默认开启）。</summary>
    public bool RunAtStartup { get; set; } = true;

    /// <summary>为 true 时，每次手动启动或开机自启均请求 UAC 以管理员身份运行（用户取消则退回普通权限）。</summary>
    public bool RunAsAdministrator { get; set; } = true;

    /// <summary>向目标窗口模拟粘贴：<see cref="PasteSimulationModes.CtrlV"/>（Ctrl+V）或 <see cref="PasteSimulationModes.ShiftInsert"/>。</summary>
    public string PasteSimulationMode { get; set; } = PasteSimulationModes.CtrlV;

    /// <summary>启动后静默访问 GitHub Releases，若有新版本则在托盘气泡提示（不弹阻断窗）。</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>启动检测已提示过的发行 tag（如 v1.2.0），避免同一版本重复气泡；升级或已最新时会清空。</summary>
    public string? LastStartupUpdateNotifiedTag { get; set; }
    public int PreviewMaxLines { get; set; } = 2;

    /// <summary>剪贴板弹窗宽度（DIP），默认与内置 XAML 一致。</summary>
    public double PopupPanelWidth { get; set; } = 420;

    /// <summary>剪贴板弹窗最大高度（DIP，列表区域随内容增高直至该上限）。</summary>
    public double PopupPanelMaxHeight { get; set; } = 560;

    /// <summary>列表每次翻过的条目数（PgUp/Dn、←→ 及翻页快捷键共用，原固定为 8）。</summary>
    public int PopupPageItems { get; set; } = 8;

    /// <summary>列表向上翻页组合键（须含修饰键，默认 Ctrl+-）。</summary>
    public uint PanelPageScrollUpModifiers { get; set; } = Win32.MOD_CONTROL;

    public uint PanelPageScrollUpKey { get; set; } = 0xBD;

    /// <summary>列表向下翻页组合键（须含修饰键，默认 Ctrl+=）。</summary>
    public uint PanelPageScrollDownModifiers { get; set; } = Win32.MOD_CONTROL;

    public uint PanelPageScrollDownKey { get; set; } = 0xBB;

    public string PanelModifierKey { get; set; } = "Ctrl";

    /// <summary>批量粘贴：Off / Fifo / Lifo（与 <see cref="BatchPasteQueueMode"/> 枚举名一致）。</summary>
    public string BatchPasteMode { get; set; } = nameof(BatchPasteQueueMode.Off);

    /// <summary>面板未打开时也可用的「批量模式」循环切换快捷键（默认 Alt+/）。须与其它全局热键不同。</summary>
    public uint BatchModeCycleHotkeyModifiers { get; set; } = Win32.MOD_ALT;

    public uint BatchModeCycleHotkeyKey { get; set; } = Win32.VK_OEM_2;

    /// <summary>多选且条目全部为文本时，是否拼成一段一次写入剪贴板并粘贴（关则逐条粘贴，便于多步撤销）。</summary>
    public bool BatchPasteMergeText { get; set; } = true;

    /// <summary>
    /// FIFO / LIFO 下队列已贴完后，在<strong>下一次</strong>他处 Ctrl+V / Shift+Insert 时自动切回普通模式（避免长期停留在队列模式）。
    /// </summary>
    public bool BatchQueueAutoSwitchToNormalAfterQueueDone { get; set; } = true;

    public List<QuickPasteEntry> QuickPastes { get; set; } = new();

    /// <summary>Ctrl+G 跳转列表顶部展示的收藏目录（Phrase 为关键词/别名，供检索）。</summary>
    public List<FolderFavoriteEntry> FolderFavorites { get; set; } = new();

    /// <summary>最近一次在「打开/保存」对话框中记录到的文件夹，供 Ctrl+G 跳转。</summary>
    public string LastFileDialogFolder { get; set; } = "";

    public string HotkeyDisplayName => FormatHotkey(HotkeyModifiers, HotkeyKey);

    public string FileJumpHotkeyDisplayName => FormatHotkey(FileJumpHotkeyModifiers, FileJumpHotkeyKey);

    public string BatchModeCycleHotkeyDisplayName => FormatHotkey(BatchModeCycleHotkeyModifiers, BatchModeCycleHotkeyKey);

    /// <summary>用于设置中单键展示（无修饰键）。</summary>
    public static string FormatSingleVk(uint vk) => VkToName(vk);

    public static string FormatHotkey(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & Win32.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & Win32.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & Win32.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & Win32.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(VkToName(key));
        return string.Join("+", parts);
    }

    private static string VkToName(uint vk) => vk switch
    {
        0xC0 => "`", 0xBD => "-", 0xBB => "=", 0xDB => "[", 0xDD => "]",
        0xDC => "\\", 0xBA => ";", 0xDE => "'", 0xBC => ",", 0xBE => ".",
        0xBF => "/", 0x20 => "Space",
        0x6B => "Num +", 0x6D => "Num -",
        >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ => $"0x{vk:X2}"
    };

    public static void NormalizePopupPanelSettings(AppSettings s)
    {
        if (s.PopupPanelWidth < 280 || s.PopupPanelWidth > 1200 || double.IsNaN(s.PopupPanelWidth))
            s.PopupPanelWidth = 420;
        if (s.PopupPanelMaxHeight < 200 || s.PopupPanelMaxHeight > 900 || double.IsNaN(s.PopupPanelMaxHeight))
            s.PopupPanelMaxHeight = 560;
        if (s.PopupPageItems < 1 || s.PopupPageItems > 50)
            s.PopupPageItems = 8;
        if (s.PanelPageScrollUpModifiers == 0)
            s.PanelPageScrollUpModifiers = Win32.MOD_CONTROL;
        if (s.PanelPageScrollDownModifiers == 0)
            s.PanelPageScrollDownModifiers = Win32.MOD_CONTROL;
        if (s.PanelPageScrollUpKey == 0)
            s.PanelPageScrollUpKey = 0xBD;
        if (s.PanelPageScrollDownKey == 0)
            s.PanelPageScrollDownKey = 0xBB;
        if (s.PanelPageScrollUpModifiers == s.PanelPageScrollDownModifiers
            && s.PanelPageScrollUpKey == s.PanelPageScrollDownKey)
        {
            s.PanelPageScrollUpKey = 0xBD;
            s.PanelPageScrollDownKey = 0xBB;
        }
    }

    private static readonly string LegacySettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClipboardManager");

    private static string SettingsDir => Path.GetDirectoryName(AppPaths.SettingsFile)!;

    private static string SettingsFile => AppPaths.SettingsFile;
    private static readonly string LegacySettingsFile = Path.Combine(LegacySettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                using (var doc = JsonDocument.Parse(json))
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
                    // 旧版 settings.json 无此字段时 Json 反序列化为 false，产品默认应为开启
                    if (!doc.RootElement.TryGetProperty(nameof(RunAtStartup), out _))
                        settings.RunAtStartup = true;
                    if (!doc.RootElement.TryGetProperty(nameof(RunAsAdministrator), out _))
                        settings.RunAsAdministrator = true;
                    if (!doc.RootElement.TryGetProperty(nameof(EnableShellNavigateInject), out _))
                        settings.EnableShellNavigateInject = true;
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpAutoOnFirstClick), out _))
                        settings.FileJumpAutoOnFirstClick = true;
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpPickerFollowMode), out _))
                    {
                        if (doc.RootElement.TryGetProperty("FileJumpPickerDockBesideDialog", out var dockEl)
                            && dockEl.ValueKind == JsonValueKind.False)
                            settings.FileJumpPickerFollowMode = FileJumpPickerFollowModes.Mouse;
                        else
                            settings.FileJumpPickerFollowMode = FileJumpPickerFollowModes.Dialog;
                    }
                    settings.FileJumpPickerFollowMode = FileJumpPickerFollowModes.Normalize(settings.FileJumpPickerFollowMode);
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpPickerAutoPopup), out _))
                        settings.FileJumpPickerAutoPopup = true;
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpPickerOpenWhenDialogForeground), out _))
                        settings.FileJumpPickerOpenWhenDialogForeground = true;
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpAutoSyncOnReturn), out _))
                        settings.FileJumpAutoSyncOnReturn = true;
                    if (!doc.RootElement.TryGetProperty(nameof(CheckUpdatesOnStartup), out _))
                        settings.CheckUpdatesOnStartup = true;
                    if (!doc.RootElement.TryGetProperty(nameof(BatchPasteMergeText), out _))
                        settings.BatchPasteMergeText = true;
                    if (!doc.RootElement.TryGetProperty(nameof(BatchQueueAutoSwitchToNormalAfterQueueDone), out _))
                    {
                        if (doc.RootElement.TryGetProperty("FifoAutoSwitchToNormalAfterQueueDone", out var legacyFifo))
                            settings.BatchQueueAutoSwitchToNormalAfterQueueDone = legacyFifo.ValueKind == JsonValueKind.True;
                        else
                            settings.BatchQueueAutoSwitchToNormalAfterQueueDone = true;
                    }
                    if (settings.FolderFavorites == null)
                        settings.FolderFavorites = new List<FolderFavoriteEntry>();
                    settings.PasteSimulationMode = PasteSimulationModes.Normalize(settings.PasteSimulationMode);
                    NormalizePopupPanelSettings(settings);
                    return settings;
                }
            }

            if (File.Exists(LegacySettingsFile))
            {
                var json = File.ReadAllText(LegacySettingsFile);
                using (var doc = JsonDocument.Parse(json))
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
                    if (!doc.RootElement.TryGetProperty(nameof(RunAtStartup), out _))
                        settings.RunAtStartup = true;
                    if (!doc.RootElement.TryGetProperty(nameof(RunAsAdministrator), out _))
                        settings.RunAsAdministrator = true;
                    if (!doc.RootElement.TryGetProperty(nameof(EnableShellNavigateInject), out _))
                        settings.EnableShellNavigateInject = true;
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpAutoOnFirstClick), out _))
                        settings.FileJumpAutoOnFirstClick = true;
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpPickerFollowMode), out _))
                    {
                        if (doc.RootElement.TryGetProperty("FileJumpPickerDockBesideDialog", out var dockEl)
                            && dockEl.ValueKind == JsonValueKind.False)
                            settings.FileJumpPickerFollowMode = FileJumpPickerFollowModes.Mouse;
                        else
                            settings.FileJumpPickerFollowMode = FileJumpPickerFollowModes.Dialog;
                    }
                    settings.FileJumpPickerFollowMode = FileJumpPickerFollowModes.Normalize(settings.FileJumpPickerFollowMode);
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpPickerAutoPopup), out _))
                        settings.FileJumpPickerAutoPopup = true;
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpPickerOpenWhenDialogForeground), out _))
                        settings.FileJumpPickerOpenWhenDialogForeground = true;
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpAutoSyncOnReturn), out _))
                        settings.FileJumpAutoSyncOnReturn = true;
                    if (!doc.RootElement.TryGetProperty(nameof(CheckUpdatesOnStartup), out _))
                        settings.CheckUpdatesOnStartup = true;
                    if (!doc.RootElement.TryGetProperty(nameof(BatchPasteMergeText), out _))
                        settings.BatchPasteMergeText = true;
                    if (!doc.RootElement.TryGetProperty(nameof(BatchQueueAutoSwitchToNormalAfterQueueDone), out _))
                    {
                        if (doc.RootElement.TryGetProperty("FifoAutoSwitchToNormalAfterQueueDone", out var legacyFifo))
                            settings.BatchQueueAutoSwitchToNormalAfterQueueDone = legacyFifo.ValueKind == JsonValueKind.True;
                        else
                            settings.BatchQueueAutoSwitchToNormalAfterQueueDone = true;
                    }
                    if (settings.FolderFavorites == null)
                        settings.FolderFavorites = new List<FolderFavoriteEntry>();
                    settings.PasteSimulationMode = PasteSimulationModes.Normalize(settings.PasteSimulationMode);
                    NormalizePopupPanelSettings(settings);
                    settings.Save();
                    return settings;
                }
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }

    public AppSettings ShallowCopy() => new()
    {
        MaxItems = MaxItems,
        HotkeyModifiers = HotkeyModifiers,
        HotkeyKey = HotkeyKey,
        FileJumpHotkeyModifiers = FileJumpHotkeyModifiers,
        FileJumpHotkeyKey = FileJumpHotkeyKey,
        FileJumpPickerShowDelayMs = FileJumpPickerShowDelayMs,
        FileJumpPickerFollowMode = FileJumpPickerFollowModes.Normalize(FileJumpPickerFollowMode),
        FileJumpPickerAutoPopup = FileJumpPickerAutoPopup,
        FileJumpPickerOpenWhenDialogForeground = FileJumpPickerOpenWhenDialogForeground,
        FileJumpAutoSyncOnReturn = FileJumpAutoSyncOnReturn,
        EnableShellNavigateInject = EnableShellNavigateInject,
        FileJumpAutoOnFirstClick = FileJumpAutoOnFirstClick,
        Theme = Theme,
        PopupPosition = PopupPosition,
        PopupOpacity = PopupOpacity,
        HideOnSameAppClick = HideOnSameAppClick,
        RunAtStartup = RunAtStartup,
        RunAsAdministrator = RunAsAdministrator,
        PasteSimulationMode = PasteSimulationMode,
        CheckUpdatesOnStartup = CheckUpdatesOnStartup,
        LastStartupUpdateNotifiedTag = LastStartupUpdateNotifiedTag,
        PreviewMaxLines = PreviewMaxLines,
        PopupPanelWidth = PopupPanelWidth,
        PopupPanelMaxHeight = PopupPanelMaxHeight,
        PopupPageItems = PopupPageItems,
        PanelPageScrollUpModifiers = PanelPageScrollUpModifiers,
        PanelPageScrollUpKey = PanelPageScrollUpKey,
        PanelPageScrollDownModifiers = PanelPageScrollDownModifiers,
        PanelPageScrollDownKey = PanelPageScrollDownKey,
        PanelModifierKey = PanelModifierKey,
        BatchPasteMode = BatchPasteMode,
        BatchModeCycleHotkeyModifiers = BatchModeCycleHotkeyModifiers,
        BatchModeCycleHotkeyKey = BatchModeCycleHotkeyKey,
        BatchPasteMergeText = BatchPasteMergeText,
        BatchQueueAutoSwitchToNormalAfterQueueDone = BatchQueueAutoSwitchToNormalAfterQueueDone,
        QuickPastes = QuickPastes.Select(q => new QuickPasteEntry { Phrase = q.Phrase, Content = q.Content }).ToList(),
        FolderFavorites = FolderFavorites.Select(f => new FolderFavoriteEntry { Phrase = f.Phrase, Path = f.Path }).ToList(),
        LastFileDialogFolder = LastFileDialogFolder
    };
}
