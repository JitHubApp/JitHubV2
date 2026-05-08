using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Base class for all block-level boxes (paragraphs, headings, lists, etc.).
/// </summary>
public abstract class BlockBox
{
    public int BlockIndex { get; set; }
    public Rect Bounds { get; protected set; }
    public Thickness Margin { get; set; }
    public bool IsDirty { get; internal set; } = true;

    /// <summary>
    /// Compute desired height for the available width and update internal state.
    /// </summary>
    public abstract float Measure(float availableWidth);

    /// <summary>
    /// Place the box at the given top-left and finalise <see cref="Bounds"/>.
    /// </summary>
    public virtual void Arrange(float x, float y, float width)
    {
        Bounds = new Rect(x, y, width, Bounds.Height);
        IsDirty = false;
    }

    /// <summary>
    /// Paint the box. Implementations must be idempotent and allocation-free on
    /// the hot path.
    /// </summary>
    public abstract void Paint(CanvasDrawingSession ds, Rect viewport);

    /// <summary>
    /// Hit test a point in document coordinates and produce a logical position.
    /// Returns false when the point is outside this block.
    /// </summary>
    public virtual bool HitTest(Point point, out Document.DocumentPosition position)
    {
        position = new Document.DocumentPosition(BlockIndex, 0, 0);
        return Bounds.Contains(point);
    }
}
