using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Images;
using System.Reflection;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SvgTilePublicationOwnershipTests
{
    [Fact]
    public void DroppedOrFailedPublicationReleasesAnUntransferredLeaseOnlyOnce()
    {
        ImageBox.SvgTilePublicationOwnership ownership = default;
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
        ImageBox.SvgTilePublicationOwnership ownership = default;
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
        Type entryType = typeof(ImageBox).GetNestedType("SvgCacheEntry", BindingFlags.NonPublic)!;
        Assert.NotNull(entryType);
        ConstructorInfo constructor = Assert.Single(entryType.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        object[] values =
        [
            new byte[] { 1, 2, 3 }, new Size(43, 29), "title", "description",
            "content-hash", "partition-hash", new MarkdownSvgDocumentInfo(43, 29),
            17L, 23L, "original-bitmap", 101, 67, new MarkdownSvgPhysicalViewport(100.25, 66.75),
        ];
        object original = constructor.Invoke(values);
        object copy = entryType.GetMethod("WithLastBitmap", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(original, ["replacement-bitmap", 151, 97, new MarkdownSvgPhysicalViewport(150.25, 96.75)])!;
        Assert.NotSame(original, copy);
        string[] names =
        [
            "RawBytes", "Intrinsic", "Title", "Desc", "ContentHash",
            "SecurityPartitionHash", "Info", "RendererIdentity", "ProviderCacheGeneration",
            "LastBitmapIdentity", "LastBitmapWidthPixels", "LastBitmapHeightPixels",
            "LastPhysicalViewport",
        ];
        object[] replacements = ["replacement-bitmap", 151, 97, new MarkdownSvgPhysicalViewport(150.25, 96.75)];
        for (int index = 0; index < names.Length; index++)
        {
            FieldInfo field = entryType.GetField(names[index], BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.NotNull(field);
            Assert.True(field.IsInitOnly);
            Assert.Equal(values[index], field.GetValue(original));
            Assert.Equal(index < 9 ? values[index] : replacements[index - 9], field.GetValue(copy));
            if (index is 0 or 6)
                Assert.Same(values[index], field.GetValue(copy));
        }
    }
}
