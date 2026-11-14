using System.Numerics;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Internal.Inlines;

/// <summary>
/// Stands in for the space before a glued symbol token so DirectWrite must
/// wrap there instead of inside the token. It has no advance or height, so
/// the line keeps its text metrics, and the space it covers would hang at the
/// end of the line anyway. The layout text is unchanged, which keeps every
/// UTF-16 offset used by selection, hit testing and accessibility.
/// </summary>
/// <remarks>
/// Declared <c>partial</c> because CsWinRT generates the AOT interop vtable
/// for classes that implement the WinRT <c>ICanvasTextInlineObject</c>
/// interface.
/// </remarks>
internal sealed partial class InlineForcedLineBreakObject : ICanvasTextInlineObject
{
    public Size Size => default;
    public float Baseline => 0f;
    public bool SupportsSideways => false;
    public Rect DrawBounds => default;
    public CanvasLineBreakCondition BreakBefore => CanvasLineBreakCondition.Neutral;
    public CanvasLineBreakCondition BreakAfter => CanvasLineBreakCondition.MustBreak;

    public void Draw(
        ICanvasTextRenderer textRenderer,
        Vector2 point,
        bool isSideways,
        bool isRightToLeft,
        object brush)
    {
        // A wrapped trailing space paints nothing.
    }
}
