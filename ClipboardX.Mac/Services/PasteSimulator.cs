using SharpHook;
using SharpHook.Native;

namespace ClipboardX.Mac;

internal sealed class PasteSimulator
{
    private readonly EventSimulator _sim = new();

    /// <summary>向前台应用发送 Cmd+V。</summary>
    public void PasteCmdV()
    {
        _sim.SimulateKeyPress(KeyCode.VcLeftMeta);
        _sim.SimulateKeyPress(KeyCode.VcV);
        _sim.SimulateKeyRelease(KeyCode.VcV);
        _sim.SimulateKeyRelease(KeyCode.VcLeftMeta);
    }
}
