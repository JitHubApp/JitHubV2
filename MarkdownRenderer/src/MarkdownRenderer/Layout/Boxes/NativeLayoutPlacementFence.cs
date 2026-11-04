using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The native layouts, retirement records, and generation an image box held
/// when a placement transfer was prepared.
/// </summary>
internal readonly struct NativeLayoutPlacementFence(
    CanvasTextLayout? Placeholder,
    NativeResourceRetirementRecord? PlaceholderRetirementRecord,
    CanvasTextLayout? Caption,
    NativeResourceRetirementRecord? CaptionRetirementRecord,
    int Generation,
    bool IsValid)
{
    public CanvasTextLayout? Placeholder { get; init; } = Placeholder;
    public NativeResourceRetirementRecord? PlaceholderRetirementRecord { get; init; } = PlaceholderRetirementRecord;
    public CanvasTextLayout? Caption { get; init; } = Caption;
    public NativeResourceRetirementRecord? CaptionRetirementRecord { get; init; } = CaptionRetirementRecord;
    public int Generation { get; init; } = Generation;
    public bool IsValid { get; init; } = IsValid;
}
