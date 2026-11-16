using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Performance.Internal.ProgressiveScenes;

/// <summary>
/// Checks for inline math placeholders: a deferred math run must occupy one
/// atomic object-replacement character that maps back to its whole source
/// formula, exactly like the run an eager parse materializes for it.
/// </summary>
internal static class InlineMathPlaceholderAssertions
{
    internal static InlineVectorSceneRun[] FindMathRuns(IEnumerable<InlineContainerBox> containers) =>
        containers
            .SelectMany(static box => box.Runs)
            .OfType<InlineVectorSceneRun>()
            .Where(static run => run.ElementKey == MarkdownElementKeys.Math)
            .ToArray();

    internal static void AssertRealizedTwinAndAtomicSourceMapping(
        InlineVectorSceneRun pending,
        InlineVectorSceneRun[] materializedMath,
        IEnumerable<InlineContainerBox> pendingContainers,
        LayoutSnapshot pendingSnapshot,
        string source)
    {
        InlineVectorSceneRun realized = Assert.Single(
            materializedMath,
            run => run.SourceSpan == pending.SourceSpan);
        Assert.Equal(1, realized.RenderedLength);
        Assert.Equal("\uFFFC", realized.Text);
        Assert.Equal(pending.AccessibleText, realized.AccessibleText);
        Assert.Null(realized.DeferredSceneMarker);

        InlineContainerBox pendingBox = Assert.Single(
            pendingContainers,
            box => box.Runs.Contains(pending));
        int start = pendingBox.GetBufferCharOffset(new DocumentPosition(
            pendingBox.BlockIndex,
            pending.InlineIndex,
            0));
        int end = pendingBox.GetBufferCharOffset(new DocumentPosition(
            pendingBox.BlockIndex,
            pending.InlineIndex,
            pending.RenderedLength));
        Assert.Equal(1, end - start);
        var selectedFormula = new DocumentRange(
            new DocumentPosition(pendingBox.BlockIndex, pending.InlineIndex, 0),
            new DocumentPosition(pendingBox.BlockIndex, pending.InlineIndex, 1));
        Assert.True(pendingSnapshot.SourceMap.TryMapRange(selectedFormula, out SourceSpan mappedSpan));
        Assert.Equal(pending.SourceSpan, mappedSpan);
        Assert.Equal(source.Substring(pending.SourceSpan.Start, pending.SourceSpan.Length),
            pendingSnapshot.SourceMap.Slice(selectedFormula));
    }
}
