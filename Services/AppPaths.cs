using System.IO;
using System.Reflection;

namespace ClipboardManager;

/// <summary>
/// 统一管理应用的所有数据路径。
/// 自解压 / U 盘等路径运行（默认）：DataRoot = exe 同级 Data\
/// 仅当进程位于按用户安装目录（%LocalAppData%\Programs\ClipboardX 下主 exe）时：DataRoot = %LocalAppData%\ClipboardX
/// 必须在 App.OnStartup 最早期调用 <see cref="Initialize"/>。
/// </summary>
internal static class AppPaths
{
#if CLIPX_FULL
    private const string ProductDirName = "ClipboardX";
    public const string MutexName = "ClipboardX_F7A2E9B0";
#elif CLIPX_CLIPBOARD
    private const string ProductDirName = "ClipboardX-clipboard";
    public const string MutexName = "ClipboardX_Clipboard_A1B2C3D4";
#elif CLIPX_FILEJUMP
    private const string ProductDirName = "ClipboardX-filejump";
    public const string MutexName = "ClipboardX_FileJump_E5F6G7H8";
#else
    private const string ProductDirName = "ClipboardX";
    public const string MutexName = "ClipboardX_F7A2E9B0";
#endif

    private static string? _dataRoot;
    private static bool _isPortable;

    /// <summary>是否使用 exe 旁 Data\ 作为数据根（非按用户安装目录运行时均为 true）。</summary>
    public static bool IsPortable => _isPortable;

    /// <summary>所有配置、数据库、日志的根目录。</summary>
    public static string DataRoot => _dataRoot ?? throw new InvalidOperationException("AppPaths.Initialize() has not been called.");

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string CustomDialogsFile => Path.Combine(DataRoot, "custom_file_dialogs.json");
    public static string SqliteDbFile => Path.Combine(DataRoot, "clipboard_history.db");
    public static string ShellNavigateLogFile => Path.Combine(DataRoot, "shell_navigate.log");
    public static string ClipboardDiagnosticsLogFile => Path.Combine(DataRoot, "clipboard_diagnostics.log");
    public static string ExplorerQuickFindLogFile => Path.Combine(DataRoot, "explorer_quickfind.log");

    /// <summary>
    /// 旧版 settings.json 所在目录（%AppData%\ClipboardX），迁移用。
    /// </summary>
    public static string LegacyRoamingDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardX");

    /// <summary>
    /// 更早期的旧版目录（%AppData%\ClipboardManager），迁移用。
    /// </summary>
    public static string LegacyClipboardManagerDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardManager");

    /// <summary>获取主程序集 / 宿主解析出的目录（单文件下多为 %TEMP%\.net\… 解压目录，不宜存放持久化数据）。</summary>
    private static string GetExeDirectory()
    {
        var loc = typeof(AppPaths).Assembly.Location;
        if (!string.IsNullOrEmpty(loc))
        {
            var d = Path.GetDirectoryName(loc);
            if (!string.IsNullOrEmpty(d)) return d;
        }
        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// 便携模式下 <c>Data\</c> 应位于用户启动的 exe 旁。单文件发布时 <see cref="AppContext.BaseDirectory"/> 指向临时解压目录，
    /// 若仍用 <see cref="GetExeDirectory"/> 会把配置与历史写入 Temp，清理后即丢失。
    /// </summary>
    private static string GetPortableDataHostDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) &&
            processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var dir = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrEmpty(dir))
                    return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                // 回退 GetExeDirectory
            }
        }

        return GetExeDirectory();
    }

    /// <summary>将旧版单文件误写在解压目录下的 Data 合并到新的 exe 旁 Data（仅缺省则复制）。</summary>
    private static void TryMigrateLegacySingleFileExtractData(string newDataRoot)
    {
        try
        {
            var legacyData = Path.Combine(GetExeDirectory(), "Data");
            if (string.Equals(
                    Path.GetFullPath(legacyData),
                    Path.GetFullPath(newDataRoot),
                    StringComparison.OrdinalIgnoreCase))
                return;
            if (!Directory.Exists(legacyData)) return;

            Directory.CreateDirectory(newDataRoot);
            foreach (var path in Directory.EnumerateFiles(legacyData))
            {
                var name = Path.GetFileName(path);
                var dest = Path.Combine(newDataRoot, name);
                if (!File.Exists(dest))
                    File.Copy(path, dest, overwrite: false);
            }
        }
        catch
        {
            /* 迁移失败不阻断启动 */
        }
    }

    /// <summary>在 App.OnStartup 最早期调用。根据是否从安装目录启动确定 DataRoot；安装布局下执行旧路径迁移。</summary>
    /// <param name="runningFromPerUserInstall">
    /// 为 true 时表示当前进程路径等于按用户安装目录中的主程序（%LocalAppData%\Programs\ClipboardX\*.exe）。
    /// </param>
    public static void Initialize(bool runningFromPerUserInstall)
    {
        if (runningFromPerUserInstall)
        {
            _isPortable = false;
            _dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductDirName);
        }
        else
        {
#if DEBUG
            // dotnet run 调试时复用安装版数据，便于开发调试
            _isPortable = false;
            _dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductDirName);
#else
            _isPortable = true;
            var hostDir = GetPortableDataHostDirectory();
            _dataRoot = Path.Combine(hostDir, "Data");
            TryMigrateLegacySingleFileExtractData(_dataRoot);
#endif
        }

        Directory.CreateDirectory(_dataRoot);

        if (!_isPortable)
            MigrateLegacyPaths();
    }

    /// <summary>
    /// 从「解压目录\Data」安装到用户 Programs 时，将尚未存在于 %LocalAppData%\{Product} 的文件复制过去，避免设置与历史丢失。
    /// </summary>
    internal static void MergePortableDataDirectoryIntoPerUserLayout(string portableDataDir)
    {
        if (string.IsNullOrEmpty(portableDataDir) || !Directory.Exists(portableDataDir))
            return;

        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductDirName);
        try
        {
            Directory.CreateDirectory(target);
            foreach (var path in Directory.EnumerateFiles(portableDataDir))
            {
                var name = Path.GetFileName(path);
                var dest = Path.Combine(target, name);
                if (!File.Exists(dest))
                    File.Copy(path, dest, overwrite: false);
            }
        }
        catch
        {
            /* 安装流程仍继续；迁移失败时新实例得到空白或旧 Local 数据 */
        }
    }

    /// <summary>
    /// 将旧版散布在 Roaming/Local 的文件迁移到统一 DataRoot。
    /// 迁移策略：仅在目标不存在时复制，写 migrated.flag 标记已完成。
    /// </summary>
    private static void MigrateLegacyPaths()
    {
        var flag = Path.Combine(DataRoot, "migrated.flag");
        if (File.Exists(flag)) return;

        try
        {
            // 从 Roaming\ClipboardX 迁移 settings.json 和 custom_file_dialogs.json
            TryCopyIfMissing(
                Path.Combine(LegacyRoamingDir, "settings.json"),
                SettingsFile);
            TryCopyIfMissing(
                Path.Combine(LegacyRoamingDir, "custom_file_dialogs.json"),
                CustomDialogsFile);

            // 从更老的 Roaming\ClipboardManager 迁移
            TryCopyIfMissing(
                Path.Combine(LegacyClipboardManagerDir, "settings.json"),
                SettingsFile);

            // Local\ClipboardX 下的文件已经在 DataRoot 中（因为安装模式 DataRoot = %LocalAppData%\ClipboardX），无需迁移

            File.WriteAllText(flag, $"Migrated at {DateTime.Now:O}");
        }
        catch
        {
            // 迁移失败不影响主流程
        }
    }

    private static void TryCopyIfMissing(string source, string dest)
    {
        try
        {
            if (File.Exists(source) && !File.Exists(dest))
            {
                var dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.Copy(source, dest, overwrite: false);
            }
        }
        catch
        {
            // 单文件迁移失败不阻断后续
        }
    }
}
