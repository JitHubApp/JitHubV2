using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Layout.Internal.Images.Svg;
using MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Svg.Tiles;

public sealed class SvgTilePublicationOwnershipTests
{
    [Fact]
    public void DroppedOrFailedPublicationReleasesAnUntransferredLeaseOnlyOnce()
    {
        SvgTilePublicationOwnership ownership = default;
        int leaseDisposeCount = 0;

        void ReleaseIfUntransferred()
        {
            if (ownership.TryReleaseUntransferred())
                leaseDisposeCount++;
        }

        // The callback's failure cleanup and PublishOnUiThread's drop cleanup
        // may both run; only the first may release the lease.
        ReleaseIfUntransferred();
        ReleaseIfUntransferred();

        Assert.Equal(1, leaseDisposeCount);
    }

    [Fact]
    public void DroppedCleanupCannotReleaseALeaseAfterDictionaryTransfer()
    {
        SvgTilePublicationOwnership ownership = default;
        int leaseDisposeCount = 0;

        Assert.True(ownership.TryTransfer());

        if (ownership.TryReleaseUntransferred())
            leaseDisposeCount++;

        Assert.Equal(0, leaseDisposeCount);

        // The dictionary, not the publication's dropped callback, now owns
        // the lease and performs its eventual release.
        leaseDisposeCount++;
        if (ownership.TryReleaseUntransferred())
            leaseDisposeCount++;

        Assert.Equal(1, leaseDisposeCount);
    }

    [Fact]
    public void SvgCacheEntryCopyKeepsSourceIdentityAndOnlyReplacesBitmapMetadata()
    {
        byte[] rawBytes = [1, 2, 3];
        var info = new MarkdownSvgDocumentInfo(43, 29);
        var original = new SvgCacheEntry(
            rawBytes, new Size(43, 29), "title", "description",
            "content-hash", "partition-hash", info,
            17L, 23L, "original-bitmap", 101, 67, new MarkdownSvgPhysicalViewport(100.25, 66.75));
        AssertSourceIdentity(original, rawBytes, info);
        Assert.Equal("original-bitmap", original.LastBitmapIdentity);
        Assert.Equal(101, original.LastBitmapWidthPixels);
        Assert.Equal(67, original.LastBitmapHeightPixels);
        Assert.Equal(new MarkdownSvgPhysicalViewport(100.25, 66.75), original.LastPhysicalViewport);

        SvgCacheEntry copy = original.WithLastBitmap(
            "replacement-bitmap", 151, 97, new MarkdownSvgPhysicalViewport(150.25, 96.75));

        Assert.NotSame(original, copy);
        AssertSourceIdentity(copy, rawBytes, info);
        Assert.Equal("replacement-bitmap", copy.LastBitmapIdentity);
        Assert.Equal(151, copy.LastBitmapWidthPixels);
        Assert.Equal(97, copy.LastBitmapHeightPixels);
        Assert.Equal(new MarkdownSvgPhysicalViewport(150.25, 96.75), copy.LastPhysicalViewport);

        // The original entry is not mutated by the copy.
        Assert.Equal("original-bitmap", original.LastBitmapIdentity);
        Assert.Equal(101, original.LastBitmapWidthPixels);
        Assert.Equal(67, original.LastBitmapHeightPixels);
        Assert.Equal(new MarkdownSvgPhysicalViewport(100.25, 66.75), original.LastPhysicalViewport);
    }

    private static void AssertSourceIdentity(SvgCacheEntry entry, byte[] rawBytes, MarkdownSvgDocumentInfo info)
    {
        Assert.Same(rawBytes, entry.RawBytes);
        Assert.Equal(new Size(43, 29), entry.Intrinsic);
        Assert.Equal("title", entry.Title);
        Assert.Equal("description", entry.Desc);
        Assert.Equal("content-hash", entry.ContentHash);
        Assert.Equal("partition-hash", entry.SecurityPartitionHash);
        Assert.Same(info, entry.Info);
        Assert.Equal(17L, entry.RendererIdentity);
        Assert.Equal(23L, entry.ProviderCacheGeneration);
    }
}
