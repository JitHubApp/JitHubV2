using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

[CollectionDefinition("SvgResourceInspectionWorkspace", DisableParallelization = true)]
public sealed class SvgResourceInspectionWorkspaceCollection
{
}

[Collection("SvgResourceInspectionWorkspace")]
public sealed class SvgRasterReferenceMemoTests
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    [Theory]
    [InlineData("image")]
    [InlineData("feImage")]
    [InlineData("css")]
    [InlineData("nested")]
    public async Task RepeatedRasterUrisKeepDeduplicatedBudgetsAcrossReferenceKinds(string kind)
    {
        byte[] png = PngWithPadding();
        string uri = DataUri(png);
        string reference = kind switch
        {
            "image" => $"<image href='{uri}'/>",
            "feImage" => $"<filter id='f'><feImage href='{uri}'/></filter>",
            "css" => $"<style>.a{{fill:url({uri})}}</style><rect class='a'/>",
            "nested" => $"<image href='{DataUri(Encoding.UTF8.GetBytes(Svg($"<image href=\"{uri}\"/>")), "image/svg+xml")}'/>",
            _ => throw new InvalidOperationException(),
        };
        SvgPreflightResult result = await InspectAsync($"<image href='{uri}'/>" + reference + reference);
        try
        {
            Assert.Equal(1, result.EmbeddedPixels);
            if (kind == "nested")
                Assert.True(result.EmbeddedBytes > png.Length);
            else
                Assert.Equal(png.Length, result.EmbeddedBytes);
        }
        finally { result.ReleaseSource(); }
    }

    [Fact]
    public async Task EquivalentPayloadWithDifferentMimeOrEncodingStillGetsValidated()
    {
        byte[] png = PngWithPadding();
        string percent = "data:false," + string.Concat(png.Select(value => $"%{value:x2}"));
        SvgPreflightResult result = await InspectAsync(
            $"<image href='{DataUri(png)}'/><image href='{DataUri(png, "image/gif")}'/><image href='{percent}'/>");
        try
        {
            Assert.Equal(png.Length, result.EmbeddedBytes);
            Assert.Equal(1, result.EmbeddedPixels);
        }
        finally { result.ReleaseSource(); }
    }

    [Fact]
    public async Task ChangedLatePayloadCannotReusePositiveValidation()
    {
        string uri = DataUri(PngWithPadding());
        string invalid = uri[..^9] + "!" + uri[^8..];
        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(() =>
            InspectAsync($"<image href='{uri}'/><image href='{invalid}'/>"));
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Fact]
    public async Task SeparateOpenHonorsLoweredBudgetAfterAnEarlierPositiveResult()
    {
        byte[] png = PngWithPadding();
        string reference = $"<image href='{DataUri(png)}'/>";
        SvgPreflightResult admitted = await InspectAsync(reference + reference);
        admitted.ReleaseSource();
        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(() =>
            InspectAsync(reference + reference, new ResvgMarkdownSvgRendererOptions(maxEmbeddedImageBytes: png.Length - 1)));
        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);
    }

    [Fact]
    public async Task RasterMemoDoesNotBypassDeeperNestedSvgValidation()
    {
        string leaf = Svg($"<image href='{DataUri(PngWithPadding())}'/>");
        string deeper = Svg($"<image href='{DataUri(Encoding.UTF8.GetBytes(leaf), "image/svg+xml")}'/>");
        string deepest = Svg($"<image href='{DataUri(Encoding.UTF8.GetBytes(deeper), "image/svg+xml")}'/>");
        string leafReference = $"<image href='{DataUri(Encoding.UTF8.GetBytes(leaf), "image/svg+xml")}'/>";
        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(() =>
            InspectAsync(leafReference + $"<image href='{DataUri(Encoding.UTF8.GetBytes(deepest), "image/svg+xml")}'/>",
                new ResvgMarkdownSvgRendererOptions(maxNestedSvgDepth: 3)));
        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);
    }

    [Fact]
    public void MemoStoresAtMost256DigestOnlyEntriesAndDoesNotEvictEarlierPositives()
    {
        using var context = new SvgResourceInspectionContext(pool: null);
        for (ulong value = 0; value < 600; value++)
            context.RememberRaster(Key(value));

        for (ulong value = 0; value < 256; value++)
            Assert.True(context.IsRasterValidated(Key(value)));
        for (ulong value = 256; value < 600; value++)
            Assert.False(context.IsRasterValidated(Key(value)));

        // The key is a value type holding only four digest words.
        Assert.True(typeof(SvgRasterReferenceKey).IsValueType);
        Type[] keyMembers = typeof(SvgRasterReferenceKey).GetProperties()
            .Select(static property => property.PropertyType)
            .ToArray();
        Assert.Equal(4, keyMembers.Length);
        Assert.All(keyMembers, static type => Assert.Equal(typeof(ulong), type));
    }

    [Fact]
    public void DisposedInspectionReturnsClearedWorkspaceAndStaleDisposeCannotClearNewBorrower()
    {
        var pool = new SvgResourceInspectionWorkspacePool();
        SvgRasterReferenceKey key = Key(42);
        var returned = new SvgResourceInspectionContext(pool);
        SvgResourceInspectionContext? borrower = null;
        try
        {
            Assert.True(returned.AddResource(key));
            returned.RememberRaster(key);
            returned.MarkNestedSvgValidated(key, 2);
            char[] returnedScratch = returned.GetAttributeScratch(8 * 1024 * 1024 + 2);
            Assert.Equal(1026, returnedScratch.Length);
            returnedScratch.AsSpan().Fill('x');

            returned.Dispose();
            borrower = new SvgResourceInspectionContext(pool);
            char[] borrowedScratch = borrower.GetAttributeScratch(8 * 1024 * 1024 + 2);
            Assert.Same(returnedScratch, borrowedScratch);
            Assert.Equal(-1, borrowedScratch.AsSpan().IndexOfAnyExcept('\0'));
            Assert.True(borrower.AddResource(key), "Returning a workspace must clear all prior resource identities.");
            Assert.False(borrower.IsRasterValidated(key), "Returning a workspace must clear raster memo entries.");
            Assert.False(borrower.IsNestedSvgValidatedAtDepth(key, 1), "Returning a workspace must clear nested-SVG depth entries.");

            // A duplicate dispose from the former owner must not clear a
            // workspace that has since been reissued to another inspection.
            borrower.RememberRaster(key);
            borrower.MarkNestedSvgValidated(key, 2);
            borrowedScratch[0] = 'y';
            returned.Dispose();
            Assert.Equal('y', borrowedScratch[0]);
            Assert.False(borrower.AddResource(key));
            Assert.True(borrower.IsRasterValidated(key));
            Assert.True(borrower.IsNestedSvgValidatedAtDepth(key, 1));
            Assert.Throws<ObjectDisposedException>(() => returned.AddResource(key));
        }
        finally
        {
            borrower?.Dispose();
            returned.Dispose();
        }
    }

    [Fact]
    public void OversizedInspectionWorkspaceIsClearedAndDroppedInsteadOfRetained()
    {
        var pool = new SvgResourceInspectionWorkspacePool();
        SvgResourceInspectionWorkspace oversized = pool.Rent();
        for (ulong value = 0; value < 512; value++)
        {
            SvgRasterReferenceKey key = Key(value);
            Assert.True(oversized.Resources.Add(key));
            oversized.DeepestValidatedNestedSvgDepth[key] = 2;
            oversized.ValidatedRasterReferences.Add(key);
        }

        long capacity = oversized.CombinedCollectionCapacity;
        Assert.True(capacity > 1024, $"The fixture must exceed the bounded pool capacity; observed {capacity}.");

        pool.Return(oversized);
        Assert.Empty(oversized.Resources);
        Assert.Empty(oversized.DeepestValidatedNestedSvgDepth);
        Assert.Empty(oversized.ValidatedRasterReferences);

        SvgResourceInspectionWorkspace borrowed = pool.Rent();
        Assert.NotSame(oversized, borrowed);
        Assert.True(borrowed.Resources.Add(Key(0)));
    }

    [Fact]
    public void RenderersDoNotShareInspectionWorkspaces()
    {
        var firstPool = new SvgResourceInspectionWorkspacePool();
        var secondPool = new SvgResourceInspectionWorkspacePool();
        SvgResourceInspectionWorkspace returned = firstPool.Rent();
        firstPool.Return(returned);

        Assert.NotSame(returned, secondPool.Rent());
        Assert.Same(returned, firstPool.Rent());
    }

    private static SvgRasterReferenceKey Key(ulong value) => new(value, 0, 0, 0);

    [Fact]
    public async Task ExactRepeatAvoidsPayloadSizedPerReferenceAllocations()
    {
        // This is host inspection, not a raster-decoder validation. The valid
        // one-pixel PNG has bounded ignored trailing data to expose URI work.
        byte[] png = PngWithPadding();
        string uri = DataUri(png);
        byte[] repeated = Encoding.UTF8.GetBytes(Svg(string.Concat(Enumerable.Repeat($"<image href='{uri}'/>", 80))));
        byte[] distinctUris = Encoding.UTF8.GetBytes(Svg(string.Concat(Enumerable.Range(0, 80).Select(index =>
            $"<image href='{DataUri(png, "image/png;x-distinct=" + index)}'/>"))));
        // InspectCore runs on the calling worker and preserves the same resource
        // identity for all variants, isolating the memo's avoided URI decoding.
        await Task.Run(() =>
        {
            long Measure(byte[] source)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Task<SvgPreflightResult> inspection = SvgPreflight.InspectCoreAsync(source, new(), CancellationToken.None);
                Assert.True(inspection.IsCompletedSuccessfully,
                    "This thread-local allocation fixture requires synchronous in-memory inspection; an async pipeline needs a process-wide measurement.");
                SvgPreflightResult result = inspection.GetAwaiter().GetResult();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                result.ReleaseSource();
                return allocated;
            }
            _ = Measure(repeated); _ = Measure(distinctUris);
            long repeatedAllocation = Measure(repeated);
            long distinctAllocation = Measure(distinctUris);
            Assert.True(distinctAllocation - repeatedAllocation >= png.Length * 70L,
                $"Exact repeats allocated {repeatedAllocation}; distinct URI validations allocated {distinctAllocation}.");
        });
    }

    private static string Svg(string contents) => $"<svg xmlns='{SvgNamespace}'>{contents}</svg>";
    private static string DataUri(byte[] bytes, string mediaType = "image/png") => $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
    private static byte[] PngWithPadding()
    {
        byte[] png = new byte[4096];
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==").CopyTo(png, 0);
        return png;
    }
    private static Task<SvgPreflightResult> InspectAsync(string contents, ResvgMarkdownSvgRendererOptions? options = null) =>
        SvgPreflight.InspectAsync(new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(Svg(contents))), options ?? new(), CancellationToken.None);
}
