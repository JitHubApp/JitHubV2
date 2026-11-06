using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Selection;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Selection and semantic-span geometry reads for the root-local table vector
/// tests, which compare cell and vector-run ranges in logical coordinates.
/// </summary>
internal static class RootLocalTableVectorQueries
{
    internal static Rect[] CaptureCellSelectionRects(LayoutSnapshot snapshot, InlineContainerBox cell)
    {
        (DocumentPosition start, DocumentPosition end) = cell.GetBlockBoundaries();
        return LayoutPlacementTestGeometry.ToLogical(
            snapshot, cell.BlockIndex, cell.GetRangeRects(new DocumentRange(start, end)));
    }

    internal static Rect[] CaptureRunSelectionRects(
        LayoutSnapshot snapshot,
        InlineContainerBox cell,
        InlineRun run)
    {
        int runIndex = Enumerable.Range(0, cell.Runs.Count)
            .Single(index => ReferenceEquals(cell.Runs[index], run));
        var range = new DocumentRange(
            new DocumentPosition(cell.BlockIndex, runIndex, 0),
            new DocumentPosition(cell.BlockIndex, runIndex, run.RenderedLength));
        return LayoutPlacementTestGeometry.ToLogical(snapshot, cell.BlockIndex, cell.GetRangeRects(range));
    }

    internal static InlineContainerBox FindOwnerCell(
        IReadOnlyList<InlineContainerBox> cells,
        InlineVectorSceneRun vector) =>
        Assert.Single(cells, cell => cell.Runs.Any(run => ReferenceEquals(run, vector)));

    /// <summary>The run's native drawing owner; a measured-placement clone has none.</summary>
    internal static VectorSceneDrawing GetVectorDrawing(InlineVectorSceneRun run) =>
        Assert.IsType<VectorSceneDrawing>(run.Drawing);

    internal static InlineVectorSceneRun[] EnumerateVectors(IEnumerable<InlineContainerBox> cells) =>
        cells.SelectMany(static cell => cell.Runs.OfType<InlineVectorSceneRun>()).ToArray();

    /// <summary>A rectangle placed by the table root at <paramref name="tableBlockIndex"/>, in logical coordinates.</summary>
    internal static Rect ToTableLogical(LayoutSnapshot snapshot, int tableBlockIndex, Rect bounds) =>
        LayoutPlacementTestGeometry.ToLogical(snapshot, tableBlockIndex, [bounds])[0];

    internal static void AssertSemanticTextAndSpanGeometryEqual(
        LayoutSnapshot actual,
        LayoutSnapshot expected)
    {
        Assert.Equal(expected.SemanticDocument.Text, actual.SemanticDocument.Text);
        var expectedSpans = CaptureSemanticSpanGeometry(expected);
        var actualSpans = CaptureSemanticSpanGeometry(actual);
        Assert.Equal(expectedSpans.Length, actualSpans.Length);
        for (int index = 0; index < expectedSpans.Length; index++)
        {
            Assert.Equal(expectedSpans[index].Start, actualSpans[index].Start);
            Assert.Equal(expectedSpans[index].End, actualSpans[index].End);
            Assert.Equal(expectedSpans[index].Text, actualSpans[index].Text);
            Assert.Equal(expectedSpans[index].RunType, actualSpans[index].RunType);
            Assert.Equal(expectedSpans[index].Rects, actualSpans[index].Rects);
        }
    }

    private static SemanticSpanGeometry[] CaptureSemanticSpanGeometry(LayoutSnapshot snapshot) =>
        snapshot.SemanticDocument.TextSpans
            .Where(static span => span.InlineBox is not null)
            .Select(span => new SemanticSpanGeometry(
                span.TextStart,
                span.TextEnd,
                span.InlineRun?.GetType(),
                snapshot.SemanticDocument.Text.Substring(span.TextStart, span.TextEnd - span.TextStart),
                snapshot.SemanticDocument.GetDocumentRects(span.TextStart, span.TextEnd).ToArray()))
            .ToArray();

    private readonly record struct SemanticSpanGeometry(
        int Start,
        int End,
        Type? RunType,
        string Text,
        Rect[] Rects);
}
