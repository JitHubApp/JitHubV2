#if MARKDOWN_LAYOUT_DIAGNOSTICS
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownSvgSubstageContractTests
{
    [Fact]
    public void BitmapAndCacheSubstageIdsRemainStableAndFitPackedStageField()
    {
        Assert.Equal(11, (int)MarkdownSvgWorkStage.SvgBitmapBufferCopy);
        Assert.Equal(12, (int)MarkdownSvgWorkStage.SvgCanvasBitmapCreateFromBytes);
        Assert.Equal(13, (int)MarkdownSvgWorkStage.SvgCacheWeightSizeQuery);
        Assert.Equal(14, (int)MarkdownSvgWorkStage.SvgCacheEntryPixelSizeQuery);
        Assert.Equal(15, (int)MarkdownSvgWorkStage.SvgCacheLruInsertAndEviction);
        Assert.Equal(16, (int)MarkdownSvgWorkStage.OuterDispatchQueueWait);
        Assert.All(
            Enum.GetValues<MarkdownSvgWorkStage>(),
            static stage => Assert.InRange((int)stage, 1, 16));
    }

    [Fact]
    public async Task WorkIdScopeFlowsAcrossAwaitAndRestoresThePreviousValue()
    {
        long previousWorkId = MarkdownSvgStageTrace.CurrentWorkId;
        var scope = new MarkdownSvgWorkScope(previousWorkId, 8721);
        MarkdownSvgStageTrace.SetCurrentWorkId(scope.WorkId);
        try
        {
            Assert.Equal(8721, MarkdownSvgStageTrace.CurrentWorkId);
            long flowedWorkId = await Task.Run(async () =>
            {
                await Task.Yield();
                return MarkdownSvgStageTrace.CurrentWorkId;
            });
            Assert.Equal(8721, flowedWorkId);
        }
        finally
        {
            scope.Dispose();
        }

        Assert.Equal(previousWorkId, MarkdownSvgStageTrace.CurrentWorkId);
    }
}
#endif
