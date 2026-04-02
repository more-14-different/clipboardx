namespace ClipboardManager;

public static class FileJumpPickerFollowModes
{
    public const string Mouse = "Mouse";
    public const string Dialog = "Dialog";

    public static bool IsDialog(string? mode) =>
        !string.Equals(mode, Mouse, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode) || IsDialog(mode)) return Dialog;
        return Mouse;
    }
}
