using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class ImageBoxInlineMeasuredPlacementTransferTests
{
    private const float Width = 320f;

    [Theory]
    [InlineData(320f)]
    [InlineData(160f)]
    public async Task InlineTransferUsesExactPerImageWidthAndKeepsDirtyInlineChildDirty(float preparedAvailableWidth)
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        ImageBox source = fixture.ImageRun.Image;
        Assert.False(fixture.Source.IsDirty);
        Assert.True(source.TryGetCompletedInlinePlacementState(
            out float sourceAvailableWidth,
            out float sourceImageWidth,
            out float sourceImageHeight));
        Assert.Equal(Width, sourceAvailableWidth);
        Assert.True(source.IsDirty);

        MarkdownLayoutContext candidateContext = fixture.CreateContext();
        ImageBox? prepared = null;
        LayoutSnapshot? sourceSnapshot = null;
        bool sourceSnapshotOwnsFixture = false;
        try
        {
            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                candidateContext,
                out prepared,
                out SharedCanvasBitmapCache.Lease? expectedSourceLease));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedSourceLease);
            Assert.True(prepared.IsDirty);

            _ = prepared.MeasureInline(preparedAvailableWidth, lineHeight: 18f);
            float preparedImageWidth = (float)prepared.Bounds.Width;
            float preparedImageHeight = (float)prepared.Bounds.Height;
            prepared.SetInlineBounds(new Windows.Foundation.Rect(
                41d, 23d, preparedImageWidth, preparedImageHeight));

            Windows.Foundation.Rect sourceBefore = source.Bounds;
            Windows.Foundation.Rect preparedBefore = prepared.Bounds;
            Assert.NotEqual(sourceBefore, preparedBefore);
            Assert.Equal(sourceImageWidth, sourceBefore.Width);
            Assert.Equal(sourceImageHeight, sourceBefore.Height);

            sourceSnapshot = new LayoutSnapshot(
                new BlockBox[] { fixture.Source },
                fixture.SourceContext.SourceMap,
                Width,
                (float)fixture.Source.Bounds.Height);
            fixture.TransferSourceOwnershipToSnapshot(sourceSnapshot);
            sourceSnapshotOwnsFixture = true;

            var transfer = new ImageBox.MeasuredPlacementTransfer(
                source,
                prepared,
                expectedSourceLease,
                ExpectedSvgMetadataFence: null,
                ExpectedInlineLayout: true,
                ExpectedAvailableWidth: sourceAvailableWidth,
                ExpectedPreparedAvailableWidth: preparedAvailableWidth == sourceAvailableWidth
                    ? 0f : preparedAvailableWidth);

            Assert.True(sourceSnapshot.TryBeginInteraction());
            try
            {
                // The batch fallback width is intentionally not the image's
                // measured width: an inline batch validates each image's own
                // captured width rather than treating it as a block width.
                Assert.True(ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    new[] { transfer },
                    currentProviderCacheGeneration: 0,
                    expectedAvailableWidth: Width + 1f));
            }
            finally
            {
                sourceSnapshot.EndInteraction();
            }

            Assert.Equal(preparedBefore, source.Bounds);
            Assert.Equal(sourceBefore, prepared.Bounds);
            Assert.True(source.TryGetCompletedInlinePlacementWidth(out float committedWidth));
            Assert.Equal(preparedAvailableWidth, committedWidth);
            Assert.True(source.IsDirty);
            Assert.True(prepared.IsDirty);
            Assert.Same(expectedSourceLease, GetField<SharedCanvasBitmapCache.Lease>(source, "_bitmapLease"));
            Assert.Same(expectedSourceLease.Bitmap, source.Bitmap);
        }
        finally
        {
            if (prepared is not null && !prepared.ImageLifetimeDisposedForTests)
            {
                candidateContext.DiscardConstructionBlock(prepared);
                await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (sourceSnapshot is not null)
                await sourceSnapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            else if (!sourceSnapshotOwnsFixture)
                await fixture.RetireSourceAsync();
        }
    }

    [Fact]
    public async Task InlineTransferRejectsWrongModeWidthAndActiveCheckpointWithoutMutation()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        ImageBox source = fixture.ImageRun.Image;
        Assert.False(fixture.Source.IsDirty);
        Assert.True(source.TryGetCompletedInlinePlacementWidth(out float availableWidth));
        MarkdownLayoutContext candidateContext = fixture.CreateContext();
        ImageBox? prepared = null;
        LayoutSnapshot? sourceSnapshot = null;

        try
        {
            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                candidateContext,
                out prepared,
                out SharedCanvasBitmapCache.Lease? expectedSourceLease));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedSourceLease);
            _ = prepared.MeasureInline(availableWidth, lineHeight: 18f);
            prepared.SetInlineBounds(new Windows.Foundation.Rect(
                51d, 31d, prepared.Bounds.Width, prepared.Bounds.Height));

            sourceSnapshot = new LayoutSnapshot(
                new BlockBox[] { fixture.Source },
                fixture.SourceContext.SourceMap,
                Width,
                (float)fixture.Source.Bounds.Height);
            fixture.TransferSourceOwnershipToSnapshot(sourceSnapshot);
            Windows.Foundation.Rect sourceBefore = source.Bounds;
            Windows.Foundation.Rect preparedBefore = prepared.Bounds;

            Assert.True(sourceSnapshot.TryBeginInteraction());
            try
            {
                // Existing/default callers remain block-only unless they
                // explicitly opt into inline placement transfer.
                var wrongMode = new ImageBox.MeasuredPlacementTransfer(
                    source, prepared, expectedSourceLease, null);
                Assert.False(ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    new[] { wrongMode }, 0, Width));

                var wrongWidth = new ImageBox.MeasuredPlacementTransfer(
                    source, prepared, expectedSourceLease, null,
                    ExpectedInlineLayout: true,
                    ExpectedAvailableWidth: availableWidth + 1f);
                Assert.False(ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    new[] { wrongWidth }, 0, Width));

                var wrongPreparedWidth = new ImageBox.MeasuredPlacementTransfer(
                    source, prepared, expectedSourceLease, null,
                    ExpectedInlineLayout: true,
                    ExpectedAvailableWidth: availableWidth,
                    ExpectedPreparedAvailableWidth: availableWidth + 1f);
                Assert.False(ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    new[] { wrongPreparedWidth }, 0, Width));
                Assert.False(ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    new[] { wrongPreparedWidth with { ExpectedPreparedAvailableWidth = float.NaN } }, 0, Width));

                ImageBox.InlineMeasureCheckpoint checkpoint = source.BeginInlineCheckpoint();
                try
                {
                    var activeCheckpoint = new ImageBox.MeasuredPlacementTransfer(
                        source, prepared, expectedSourceLease, null,
                        ExpectedInlineLayout: true,
                        ExpectedAvailableWidth: availableWidth);
                    Assert.False(ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                        new[] { activeCheckpoint }, 0, Width));
                    Assert.False(source.TryGetCompletedInlinePlacementWidth(out _));
                }
                finally
                {
                    checkpoint.Rollback();
                }
            }
            finally
            {
                sourceSnapshot.EndInteraction();
            }

            Assert.Equal(sourceBefore, source.Bounds);
            Assert.Equal(preparedBefore, prepared.Bounds);
            Assert.True(source.IsDirty);
            Assert.True(prepared.IsDirty);
        }
        finally
        {
            if (prepared is not null && !prepared.ImageLifetimeDisposedForTests)
            {
                candidateContext.DiscardConstructionBlock(prepared);
                await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (sourceSnapshot is not null)
                await sourceSnapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            else
                await fixture.RetireSourceAsync();
        }
    }

    private static T GetField<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(
            name,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(instance));
    }
}
