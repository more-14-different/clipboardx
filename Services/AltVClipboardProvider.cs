using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace ClipboardManager;

internal static class AltVClipboardProvider
{
    private const string ModeArg = "--altv-provider-settext";
    private const string RequestArg = "--request-file";
    private const string ResultArg = "--result-file";
    private const string StopArg = "--stop-file";
    private const int ProviderRetries = 18;
    private const int ProviderDelayMs = 75;
    private const int ProviderHoldTimeoutMs = 8_000;
    private const int ProviderResultTimeoutMs = 5_000;

    internal readonly record struct Result(bool Success, bool ClipboardLocked, string Error);

    internal sealed class Session : IAsyncDisposable
    {
        private readonly Process? _process;
        private readonly string _requestFile;
        private readonly string _resultFile;
        private readonly string _stopFile;
        private bool _disposed;

        public Session(Result result, Process? process, string requestFile, string resultFile, string stopFile)
        {
            Result = result;
            _process = process;
            _requestFile = requestFile;
            _resultFile = resultFile;
            _stopFile = stopFile;
        }

        public Result Result { get; }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            try
            {
                if (_process is { HasExited: false })
                {
                    TryTouch(_stopFile);
                    using var cts = new CancellationTokenSource(1500);
                    try
                    {
                        await _process.WaitForExitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        try { _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    }
                }
            }
            finally
            {
                _process?.Dispose();
                TryDelete(_requestFile);
                TryDelete(_resultFile);
                TryDelete(_stopFile);
            }
        }
    }

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
        var stopFile = GetArgValue(args, StopArg);
        if (string.IsNullOrWhiteSpace(requestFile) || string.IsNullOrWhiteSpace(resultFile) || string.IsNullOrWhiteSpace(stopFile))
        {
            exitCode = 2;
            return true;
        }

        try
        {
            var text = File.ReadAllText(requestFile, new UTF8Encoding(false));
            var result = TrySetClipboardText(text);
            WriteResult(resultFile, result);
            if (result.Success)
            {
                ClipboardDiagnosticsLog.Write($"provider hold begin len={text.Length} stop=\"{stopFile}\"");
                HoldClipboardProviderSession(stopFile, ProviderHoldTimeoutMs);
                ClipboardDiagnosticsLog.Write($"provider hold end len={text.Length}");
            }
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

    public static async Task<Session> StartTextSessionAsync(string text)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ClipboardX", "altv-provider");
        Directory.CreateDirectory(dir);

        var token = Guid.NewGuid().ToString("N");
        var requestFile = Path.Combine(dir, $"request-{token}.txt");
        var resultFile = Path.Combine(dir, $"result-{token}.json");
        var stopFile = Path.Combine(dir, $"stop-{token}.signal");
        await File.WriteAllTextAsync(requestFile, text, new UTF8Encoding(false));

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return new Session(new Result(false, false, "ProcessPath unavailable"), null, requestFile, resultFile, stopFile);

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
        psi.ArgumentList.Add(StopArg);
        psi.ArgumentList.Add(stopFile);

        var process = Process.Start(psi);
        if (process == null)
            return new Session(new Result(false, false, "Process.Start returned null"), null, requestFile, resultFile, stopFile);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ProviderResultTimeoutMs)
        {
            if (File.Exists(resultFile))
            {
                var payload = JsonSerializer.Deserialize<Payload>(await File.ReadAllTextAsync(resultFile, Encoding.UTF8));
                if (payload == null)
                    return new Session(new Result(false, false, "Provider result parse failed"), process, requestFile, resultFile, stopFile);

                return new Session(
                    new Result(payload.Success, payload.ClipboardLocked, payload.Error ?? ""),
                    process,
                    requestFile,
                    resultFile,
                    stopFile);
            }

            if (process.HasExited)
                break;

            await Task.Delay(25);
        }

        return new Session(
            new Result(false, false, process.HasExited ? $"Provider exited ({process.ExitCode}) without result" : "Provider result timeout"),
            process,
            requestFile,
            resultFile,
            stopFile);
    }

    private static Result TrySetClipboardText(string text)
    {
        Exception? last = null;
        for (var i = 0; i < ProviderRetries; i++)
        {
            try
            {
                Win32.CloseClipboard();
                var dataObject = new WinForms.DataObject();
                dataObject.SetData(WinForms.DataFormats.UnicodeText, true, text);
                WinForms.Clipboard.SetDataObject(dataObject, true, 1, ProviderDelayMs);
                ClipboardDiagnosticsLog.Write($"provider SetDataObject ok len={text.Length} attempt={i + 1}/{ProviderRetries}");
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
                    $"provider SetDataObject fail attempt={i + 1}/{ProviderRetries} {ex.GetType().Name}: {ex.Message}{hr}");
                if (locked)
                    ClipboardDiagnosticsLog.Write($"provider owner {DescribeOpenClipboardOwner()}");

                if (i >= ProviderRetries - 1)
                    break;

                PumpOnce();
                Thread.Sleep(ProviderDelayMs);
            }
        }

        return new Result(false, last is not null && IsClipboardCantOpen(last), last?.Message ?? "Unknown provider failure");
    }

    private static void HoldClipboardProviderSession(string stopFile, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (File.Exists(stopFile))
                return;

            PumpOnce();
            Thread.Sleep(15);
        }
    }

    private static void PumpOnce()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
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

    private static void TryTouch(string path)
    {
        try
        {
            File.WriteAllText(path, "stop", new UTF8Encoding(false));
        }
        catch
        {
            /* ignore */
        }
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
