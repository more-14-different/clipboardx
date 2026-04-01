namespace ClipboardManager;

/// <summary>在通过剪贴板读取 TC/XY 等路径时，避免写入剪贴板被当作新的历史条目。</summary>
internal static class ClipboardGate
{
    private static int _depth;

    public static void Enter() => System.Threading.Interlocked.Increment(ref _depth);

    public static void Exit() => System.Threading.Interlocked.Decrement(ref _depth);

    public static bool IsActive => _depth > 0;
}
