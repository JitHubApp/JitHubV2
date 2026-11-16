using Microsoft.Graphics.Canvas.Text;
using MarkdownRenderer.Layout.Internal.Inlines;

namespace MarkdownRenderer.Layout.Internal.CodeBlocks;

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
