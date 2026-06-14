using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ClipboardManager;

internal static class AltVClipboardProvider
{
    private const string ModeArg = "--altv-provider-settext";
    private const string RequestArg = "--request-file";
    private const string ResultArg = "--result-file";
    private const int ProviderRetries = 18;
    private const int ProviderDelayMs = 75;

    internal readonly record struct Result(bool Success, bool ClipboardLocked, string Error);

    private sealed class Payload
    {
        public bool Success { get; set; }
        public bool ClipboardLocked { get; set; }
        public string Error { get; set; } = "";
    }

    public static bool TryHandleCommandLine(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !string.Equals(args[0], ModeArg, StringComparison.OrdinalIgnoreCase))
            return false;

        var requestFile = GetArgValue(args, RequestArg);
        var resultFile = GetArgValue(args, ResultArg);
        if (string.IsNullOrWhiteSpace(requestFile) || string.IsNullOrWhiteSpace(resultFile))
        {
            exitCode = 2;
            return true;
        }

        try
        {
            var text = File.ReadAllText(requestFile, new UTF8Encoding(false));
            var result = TrySetClipboardText(text);
            WriteResult(resultFile, result);
            exitCode = result.Success ? 0 : 1;
            return true;
        }
        catch (Exception ex)
        {
            WriteResult(resultFile, new Result(false, false, $"{ex.GetType().Name}: {ex.Message}"));
            exitCode = 1;
            return true;
        }
    }

    public static async Task<Result> TrySetTextFromSeparateProcessAsync(string text)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ClipboardX", "altv-provider");
        Directory.CreateDirectory(dir);

        var token = Guid.NewGuid().ToString("N");
        var requestFile = Path.Combine(dir, $"request-{token}.txt");
        var resultFile = Path.Combine(dir, $"result-{token}.json");
        await File.WriteAllTextAsync(requestFile, text, new UTF8Encoding(false));

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return new Result(false, false, "ProcessPath unavailable");

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.ArgumentList.Add(ModeArg);
            psi.ArgumentList.Add(RequestArg);
            psi.ArgumentList.Add(requestFile);
            psi.ArgumentList.Add(ResultArg);
            psi.ArgumentList.Add(resultFile);

            using var process = Process.Start(psi);
            if (process == null)
                return new Result(false, false, "Process.Start returned null");

            await process.WaitForExitAsync();

            if (!File.Exists(resultFile))
                return new Result(false, false, $"Result file missing (exit={process.ExitCode})");

            var payload = JsonSerializer.Deserialize<Payload>(await File.ReadAllTextAsync(resultFile, Encoding.UTF8));
            if (payload == null)
                return new Result(false, false, "Provider result parse failed");

            return new Result(payload.Success, payload.ClipboardLocked, payload.Error ?? "");
        }
        finally
        {
            TryDelete(requestFile);
            TryDelete(resultFile);
        }
    }

    private static Result TrySetClipboardText(string text)
    {
        Exception? last = null;
        for (var i = 0; i < ProviderRetries; i++)
        {
            try
            {
                Win32.CloseClipboard();
                System.Windows.Clipboard.SetText(text);
                ClipboardDiagnosticsLog.Write($"provider SetText ok len={text.Length} attempt={i + 1}/{ProviderRetries}");
                return new Result(true, false, "");
            }
            catch (Exception ex)
            {
                last = ex;
                var locked = IsClipboardCantOpen(ex);
                var hr = ex is System.Runtime.InteropServices.COMException com
                    ? $" hr=0x{(uint)com.HResult:X8}"
                    : "";
                ClipboardDiagnosticsLog.Write(
                    $"provider SetText fail attempt={i + 1}/{ProviderRetries} {ex.GetType().Name}: {ex.Message}{hr}");
                if (locked)
                    ClipboardDiagnosticsLog.Write($"provider owner {DescribeOpenClipboardOwner()}");

                if (i >= ProviderRetries - 1)
                    break;

                Thread.Sleep(ProviderDelayMs);
            }
        }

        return new Result(false, last is not null && IsClipboardCantOpen(last), last?.Message ?? "Unknown provider failure");
    }

    private static string? GetArgValue(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static bool IsClipboardCantOpen(Exception ex) =>
        ex is System.Runtime.InteropServices.COMException com && com.HResult == unchecked((int)0x800401D0);

    private static string DescribeOpenClipboardOwner()
    {
        var owner = Win32.GetOpenClipboardWindow();
        if (owner == IntPtr.Zero)
            return "owner=NONE";

        var title = Win32.GetWindowText(owner);
        var cls = Win32.GetWindowClassName(owner);
        _ = Win32.GetWindowThreadProcessId(owner, out var pid);
        var procName = "";
        if (pid != 0)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                procName = p.ProcessName;
            }
            catch
            {
                procName = "?";
            }
        }

        return $"owner=0x{owner.ToInt64():X} pid={pid} proc={procName} class={cls} title=\"{title}\"";
    }

    private static void WriteResult(string path, Result result)
    {
        var payload = new Payload
        {
            Success = result.Success,
            ClipboardLocked = result.ClipboardLocked,
            Error = result.Error
        };
        var json = JsonSerializer.Serialize(payload);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* ignore */
        }
    }
}
