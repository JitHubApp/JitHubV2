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
    public void TileCallbackTransfersOnlyAfterInsertionAndAlwaysCleansDetachedOwnership()
    {
        string source = File.ReadAllText(FindImageBoxSource());
        int publication = source.IndexOf(
            "SvgTilePublicationOwnership publicationOwnership = default;",
            StringComparison.Ordinal);
        int callbackEnd = source.IndexOf(
            "catch (OperationCanceledException) when (",
            publication,
            StringComparison.Ordinal);
        Assert.True(publication >= 0 && callbackEnd > publication);

        string callback = source[publication..callbackEnd];
        int add = callback.IndexOf("_svgTiles.Add(key, publishedLease);", StringComparison.Ordinal);
        int transfer = callback.IndexOf("publicationOwnership.TryTransfer()", StringComparison.Ordinal);
        int cleanupFinally = callback.IndexOf("finally", add, StringComparison.Ordinal);

        Assert.True(add >= 0 && transfer > add);
        Assert.True(cleanupFinally > transfer);
        Assert.Contains("_svgTiles.Remove(key, out previous);", callback, StringComparison.Ordinal);
        Assert.Contains("TryImageCleanup(replacedTile.Dispose);", callback, StringComparison.Ordinal);
        Assert.Contains("ReleaseUntransferredLease();", callback, StringComparison.Ordinal);
        Assert.Contains("ReleaseUntransferredLease);", callback, StringComparison.Ordinal);
    }

    [Fact]
    public void SvgCacheEntryCopyPreservesEverySourceFieldWithoutRecordSynthesis()
    {
        string source = File.ReadAllText(FindImageBoxSource());
        int begin = source.IndexOf("private sealed class SvgCacheEntry(", StringComparison.Ordinal);
        int end = source.IndexOf("internal struct SvgTilePublicationOwnership", begin, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin);
        string entry = source[begin..end];

        Assert.DoesNotContain("sealed record SvgCacheEntry", entry, StringComparison.Ordinal);
        string[] fields =
        {
            "RawBytes",
            "Intrinsic",
            "Title",
            "Desc",
            "ContentHash",
            "SecurityPartitionHash",
            "Info",
            "RendererIdentity",
            "ProviderCacheGeneration",
            "LastBitmapIdentity",
            "LastBitmapWidthPixels",
            "LastBitmapHeightPixels",
            "LastPhysicalViewport",
        };
        foreach (string field in fields)
        {
            Assert.Contains($"internal readonly {GetPropertyType(field)} {field} = {field};", entry, StringComparison.Ordinal);
        }

        foreach (string copiedSourceField in fields[..9])
            Assert.Contains($"                {copiedSourceField},", entry, StringComparison.Ordinal);
        Assert.Contains("                bitmapIdentity,", entry, StringComparison.Ordinal);
        Assert.Contains("                bitmapWidthPixels,", entry, StringComparison.Ordinal);
        Assert.Contains("                bitmapHeightPixels,", entry, StringComparison.Ordinal);
        Assert.Contains("                physicalViewport);", entry, StringComparison.Ordinal);
        Assert.Contains("string? LastBitmapIdentity = null,", entry, StringComparison.Ordinal);
        Assert.Contains("int LastBitmapWidthPixels = 0,", entry, StringComparison.Ordinal);
        Assert.Contains("int LastBitmapHeightPixels = 0,", entry, StringComparison.Ordinal);
        Assert.Contains("MarkdownSvgPhysicalViewport? LastPhysicalViewport = null)", entry, StringComparison.Ordinal);
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

    private static string GetPropertyType(string field) => field switch
    {
        "RawBytes" => "byte[]",
        "Intrinsic" => "Size",
        "Title" or "Desc" or "LastBitmapIdentity" => "string?",
        "ContentHash" or "SecurityPartitionHash" => "string",
        "Info" => "MarkdownSvgDocumentInfo",
        "RendererIdentity" or "ProviderCacheGeneration" => "long",
        "LastBitmapWidthPixels" or "LastBitmapHeightPixels" => "int",
        "LastPhysicalViewport" => "MarkdownSvgPhysicalViewport?",
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static string FindImageBoxSource()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string direct = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "MarkdownRenderer",
                "Layout",
                "Boxes",
                "ImageBox.cs");
            if (File.Exists(direct))
                return direct;

            string nested = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "Layout",
                "Boxes",
                "ImageBox.cs");
            if (File.Exists(nested))
                return nested;
        }

        throw new FileNotFoundException("Could not locate ImageBox.cs from the test base directory.");
    }
}
