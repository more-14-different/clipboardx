using System.Threading.Tasks;
using SharpHook;
using SharpHook.Native;

namespace ClipboardX.Mac;

/// <summary>默认 Ctrl+`（反引号）切换面板；macOS 需在「隐私与安全性 → 辅助功能」授予终端 / ClipboardX。</summary>
internal sealed class GlobalHotKeyService : IDisposable
{
    private readonly TaskPoolGlobalHook _hook = new();

    private readonly Action _toggle;

    public GlobalHotKeyService(Action toggle)
    {
        _toggle = toggle;
        _hook.KeyPressed += OnKeyPressed;
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        var mask = e.RawEvent.Mask;
        var ctrl = mask.HasFlag(ModifierMask.LeftCtrl) || mask.HasFlag(ModifierMask.RightCtrl);
        if (ctrl && e.Data.KeyCode == KeyCode.VcBackQuote)
            _toggle();
    }

    public void Start()
    {
        Task.Run(async () =>
        {
            try
            {
                await _hook.RunAsync().ConfigureAwait(false);
            }
            catch (HookException)
            {
                /* 无辅助功能权限或多实例时可能发生 */
            }
            catch (ObjectDisposedException)
            {
                /* 退出 */
            }
        });
    }

    public void Dispose() => _hook.Dispose();
}
