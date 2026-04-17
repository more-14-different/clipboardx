namespace ClipboardManager;

/// <summary>写入剪贴板后向目标窗口模拟的粘贴按键（与 FIFO 出队监听无关）。</summary>
public static class PasteSimulationModes
{
    public const string CtrlV = "CtrlV";
    public const string ShiftInsert = "ShiftInsert";

    public static bool IsValid(string? m) =>
        m is CtrlV or ShiftInsert;

    public static string Normalize(string? m) => IsValid(m) ? m! : CtrlV;
}
