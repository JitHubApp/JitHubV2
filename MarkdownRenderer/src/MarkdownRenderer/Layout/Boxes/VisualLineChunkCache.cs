using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>Per-chunk inputs that let the visual-line index reuse its template across arranges.</summary>
internal struct VisualLineChunkCache
{
    public InlineContainerBox? Chunk;
    public CanvasLineMetrics[]? Metrics;
    public int TextOffset;
    public int TextLength;
    public double OriginY;
    public int StartIndex;
    public int LineCount;
    public bool OriginUnchanged;
}
