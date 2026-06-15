using System;
using System.Threading;
using System.Windows.Threading;

namespace ClipboardManager.Services;

/// <summary>
/// 专用后台线程，提供给系统底层全局钩子（WH_KEYBOARD_LL / WH_MOUSE_LL）使用。
/// 隔离 WPF 主 UI 线程卡顿，防止钩子超时或导致系统输入假死。
/// </summary>
public static class GlobalHookDispatcher
{
    private static Thread? s_thread;
    private static Dispatcher? s_dispatcher;
    private static readonly ManualResetEventSlim s_initEvent = new(false);

    public static Dispatcher Dispatcher
    {
        get
        {
            if (s_thread == null)
            {
                lock (s_initEvent)
                {
                    if (s_thread == null)
                    {
                        s_thread = new Thread(() =>
                        {
                            s_dispatcher = Dispatcher.CurrentDispatcher;
                            s_initEvent.Set();
                            Dispatcher.Run();
                        })
                        {
                            Name = "ClipboardX_GlobalHookThread",
                            IsBackground = true
                        };
                        s_thread.SetApartmentState(ApartmentState.STA);
                        s_thread.Start();
                    }
                }
            }
            s_initEvent.Wait();
            return s_dispatcher!;
        }
    }
}
