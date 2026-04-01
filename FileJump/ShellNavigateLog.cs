using System.IO;
using System.Text;

namespace ClipboardManager;

/// <summary>
/// 与原生 ShellNavigate DLL 共用路径规则：<see cref="Environment.SpecialFolder.LocalApplicationData"/>下的 ClipboardX\shell_navigate.log
/// </summary>
internal static class ShellNavigateLog
{
    private static readonly object Gate = new();
    private static readonly int MaxBytesBeforeTrim = 2_000_000;

    public static string LogFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipboardX",
            "shell_navigate.log");

    public static void Write(string source, string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
                TrimIfHuge();
            }
        }
        catch
        {
            /* 日志本身不得影响主流程 */
        }
    }

    public static void WriteInjector(string message) => Write("inject", message);

    public static void WriteInjectorWin32(string message, int? lastError = null)
    {
        if (lastError.HasValue)
            Write("inject", $"{message} (Win32={lastError.Value})");
        else
            Write("inject", message);
    }

    private static void TrimIfHuge()
    {
        try
        {
            var fi = new FileInfo(LogFilePath);
            if (!fi.Exists || fi.Length <= MaxBytesBeforeTrim) return;
            // 保留尾部约一半，避免无限增长
            using var fs = new FileStream(LogFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var keep = (int)Math.Min(MaxBytesBeforeTrim / 2, fi.Length);
            var buf = new byte[keep];
            fs.Seek(-keep, SeekOrigin.End);
            fs.ReadExactly(buf, 0, keep);
            fs.SetLength(0);
            fs.Write(buf, 0, keep);
        }
        catch
        {
            /* ignore */
        }
    }
}
