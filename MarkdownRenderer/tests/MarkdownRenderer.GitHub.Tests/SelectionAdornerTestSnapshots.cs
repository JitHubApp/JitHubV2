using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Snapshots, selections and WARP frames shared by the selection-adornment admission tests.
/// </summary>
internal static class SelectionAdornerTestSnapshots
{
    public static void PaintWholeTarget(CanvasRenderTarget target, Color color)
    {
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        drawing.Clear(color);
    }

    public static SelectionController CreateSelection()
    {
        var selection = new SelectionController();
        selection.SetAnchor(new DocumentPosition(1, 0, 0));
        selection.ExtendTo(new DocumentPosition(1, 0, 1));
        return selection;
    }

    public static LayoutSnapshot CreateLazySnapshot(
        BlockBox block,
        string source,
        float width,
        float height,
        double viewportHeight)
        => CreateLazySnapshot(new[] { block }, source, width, height, viewportHeight);

    public static LayoutSnapshot CreateLazySnapshot(
        BlockBox[] blocks,
        string source,
        float width,
        float height,
        double viewportHeight)
    {
        var snapshot = new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(source),
            width: width,
            height: height);
        snapshot.EnableLazyLayout(
            availableWidth: width,
            viewportTop: 10_000,
            viewportHeight: viewportHeight,
            overscan: 0,
            CancellationToken.None);
        return snapshot;
    }

    public static LayoutSnapshot CreateReadySnapshot(string source)
    {
        var block = new SelectionAdornerFixedBox(new Rect(4, 8, 30, 12)) { BlockIndex = 1 };
        block.Measure(120);
        block.Arrange(4, 8, 120);
        return new LayoutSnapshot(
            new BlockBox[] { block },
            new MarkdownSourceMap(source),
            width: 120,
            height: 80);
    }
}
