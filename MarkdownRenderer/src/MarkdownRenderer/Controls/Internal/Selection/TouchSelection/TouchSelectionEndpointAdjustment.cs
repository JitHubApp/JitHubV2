
namespace MarkdownRenderer.Controls.Internal.Selection.TouchSelection;

internal readonly struct TouchSelectionEndpointAdjustment
{
    public TouchSelectionEndpointAdjustment(int Offset, bool IsStartEndpoint)
    {
        this.Offset = Offset;
        this.IsStartEndpoint = IsStartEndpoint;
    }

    public int Offset { get; init; }
    public bool IsStartEndpoint { get; init; }
}
