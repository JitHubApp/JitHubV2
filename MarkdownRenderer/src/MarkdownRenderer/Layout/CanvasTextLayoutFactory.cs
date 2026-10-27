#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Keeps diagnostic timing around the native Win2D/DirectWrite constructor itself,
/// without adding a clock read to normal layout builds.
/// </summary>
internal static class CanvasTextLayoutFactory
{
    internal static CanvasTextLayout Create(
        ICanvasResourceCreator resourceCreator,
        string text,
        CanvasTextFormat format,
        float width,
        float height,
        MarkdownLayoutDiagnostics? diagnostics)
    {
        if (diagnostics is null || diagnostics.IsCaptured)
        {
            return new CanvasTextLayout(resourceCreator, text, format, width, height);
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            return new CanvasTextLayout(resourceCreator, text, format, width, height);
        }
        finally
        {
            diagnostics.RecordCanvasTextLayoutConstructor(Stopwatch.GetTimestamp() - started);
        }
    }
}
#endif
