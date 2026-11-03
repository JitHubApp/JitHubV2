using Windows.Foundation;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// The root automation peer's own bounding rectangle, which WinUI clips to the
/// ancestor scroll viewers. The peer implements this explicitly so screen
/// geometry can read the protected override without exposing it.
/// </summary>
internal interface IMarkdownAutomationClipSource
{
    Rect GetClippedBoundingRectangle();
}
