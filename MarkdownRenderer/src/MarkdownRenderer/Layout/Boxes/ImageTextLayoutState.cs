using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// A snapshot of an image box's placeholder and caption layouts and their
/// retirement records, read under the measure lock.
/// </summary>
internal readonly record struct ImageTextLayoutState(
    CanvasTextLayout? Placeholder,
    NativeResourceRetirementRecord? PlaceholderRecord,
    CanvasTextLayout? Caption,
    NativeResourceRetirementRecord? CaptionRecord,
    bool HasValidCustody);
