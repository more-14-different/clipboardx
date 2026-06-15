using System.Diagnostics;

namespace ClipboardManager;

internal sealed class AltVTextPasteSession
{
    internal readonly record struct ClipboardWriteResult(bool Success, bool ClipboardLocked);

    internal readonly record struct ClipboardPrepareResult(
        ClipboardWriteResult Result,
        AltVClipboardProvider.Session? ProviderSession);

    private readonly IntPtr _targetWindow;
    private readonly bool _useExternalClipboardProvider;
    private readonly string _configuredPasteMode;
    private readonly Func<string, int, int, Task<ClipboardWriteResult>> _setClipboardTextAsync;
    private readonly Func<string, bool> _tryDirectInsert;
    private readonly Func<string, bool> _tryDirectType;
    private readonly Action _sendCtrlVPaste;
    private readonly Action _sendShiftInsertPaste;

    public AltVTextPasteSession(
        IntPtr targetWindow,
        bool useExternalClipboardProvider,
        string configuredPasteMode,
        Func<string, int, int, Task<ClipboardWriteResult>> setClipboardTextAsync,
        Func<string, bool> tryDirectInsert,
        Func<string, bool> tryDirectType,
        Action sendCtrlVPaste,
        Action sendShiftInsertPaste)
    {
        _targetWindow = targetWindow;
        _useExternalClipboardProvider = useExternalClipboardProvider;
        _configuredPasteMode = configuredPasteMode;
        _setClipboardTextAsync = setClipboardTextAsync;
        _tryDirectInsert = tryDirectInsert;
        _tryDirectType = tryDirectType;
        _sendCtrlVPaste = sendCtrlVPaste;
        _sendShiftInsertPaste = sendShiftInsertPaste;
    }

    public async Task<ClipboardPrepareResult> PrepareClipboardAsync(
        string text,
        string logTag,
        int maxRetries,
        int delayMs)
    {
        if (_useExternalClipboardProvider)
        {
            var providerSession = await AltVClipboardProvider.StartTextSessionAsync(text);
            var providerResult = providerSession.Result;
            ClipboardDiagnosticsLog.Write(
                $"{logTag} clipboardProvider ok={providerResult.Success} locked={providerResult.ClipboardLocked} len={text.Length}" +
                (string.IsNullOrWhiteSpace(providerResult.Error) ? "" : $" err=\"{providerResult.Error}\""));
            return new ClipboardPrepareResult(
                new ClipboardWriteResult(providerResult.Success, providerResult.ClipboardLocked),
                providerSession);
        }

        var directResult = await _setClipboardTextAsync(text, maxRetries, delayMs);
        ClipboardDiagnosticsLog.Write(
            $"{logTag} clipboardPrimary ok={directResult.Success} locked={directResult.ClipboardLocked} len={text.Length}");
        return new ClipboardPrepareResult(directResult, null);
    }

    public bool TryInsertWithoutClipboard(string text, string logTag, out bool usedNonClipboardTextInsert)
    {
        usedNonClipboardTextInsert = false;

        var ok = _tryDirectInsert(text);
        usedNonClipboardTextInsert = ok;
        ClipboardDiagnosticsLog.Write($"{logTag} directInsert ok={ok} len={text.Length}");
        if (ok)
            return true;

        ok = _tryDirectType(text);
        usedNonClipboardTextInsert = ok;
        ClipboardDiagnosticsLog.Write($"{logTag} directUnicode ok={ok} len={text.Length}");
        return ok;
    }

    public PasteTargetHeuristics.PasteDispatchDecision DispatchPaste()
    {
        var decision = PasteTargetHeuristics.DecideMode(_targetWindow, _configuredPasteMode);

        if (decision.Mode == PasteSimulationModes.ShiftInsert)
            _sendShiftInsertPaste();
        else
            _sendCtrlVPaste();

        return decision;
    }

    public async Task AwaitProviderSettleAsync(bool noSegmentDelays)
    {
        var minHoldMs = noSegmentDelays ? 55 : 85;
        var maxHoldMs = noSegmentDelays ? 120 : 240;
        var stableAfterForegroundShiftMs = 45;
        var sw = Stopwatch.StartNew();
        var switchedAwayAtMs = -1L;

        while (sw.ElapsedMilliseconds < maxHoldMs)
        {
            await Task.Delay(15);

            var fg = Win32.GetForegroundWindow();
            if (sw.ElapsedMilliseconds < minHoldMs)
                continue;

            if (_targetWindow == IntPtr.Zero || !Win32.IsWindow(_targetWindow))
                break;

            if (fg != IntPtr.Zero && fg != _targetWindow)
            {
                if (switchedAwayAtMs < 0)
                    switchedAwayAtMs = sw.ElapsedMilliseconds;
                else if (sw.ElapsedMilliseconds - switchedAwayAtMs >= stableAfterForegroundShiftMs)
                    break;
            }
            else
            {
                switchedAwayAtMs = -1;
            }
        }

        ClipboardDiagnosticsLog.Write(
            $"paste providerSettle elapsedMs={sw.ElapsedMilliseconds} target=0x{_targetWindow.ToInt64():X} mode={_configuredPasteMode}");
    }

    public void LogSuccessPath(string logTag, string path, PasteTargetHeuristics.PasteDispatchDecision dispatch)
    {
        ClipboardDiagnosticsLog.Write(
            $"{logTag} success path={path} shortcut={dispatch.Mode} reason={dispatch.Reason} " +
            $"target=0x{_targetWindow.ToInt64():X} proc={dispatch.ProcessName} class={dispatch.WindowClass} title=\"{dispatch.WindowTitle}\"");
    }
}
