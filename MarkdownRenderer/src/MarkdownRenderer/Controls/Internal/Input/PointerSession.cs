namespace MarkdownRenderer.Controls.Internal.Input;

internal readonly struct PointerSession(uint PointerId, bool IsPrimary)
{
    public readonly uint PointerId = PointerId;
    public readonly bool IsPrimary = IsPrimary;
    public bool IsActive => PointerId != 0;
}
