using System.Collections;
using System.Reflection;
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
        Type contextType = typeof(SvgPreflight).GetNestedType("ResourceInspectionContext", BindingFlags.NonPublic)!;
        Type keyType = typeof(SvgPreflight).GetNestedType("RasterReferenceKey", BindingFlags.NonPublic)!;
        object context = Activator.CreateInstance(contextType, nonPublic: true)!;
        MethodInfo remember = contextType.GetMethod("RememberRaster", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo contains = contextType.GetMethod("IsRasterValidated", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object Key(ulong value) => Activator.CreateInstance(keyType, value, 0UL, 0UL, 0UL)!;
        try
        {
            for (ulong value = 0; value < 600; value++)
                remember.Invoke(context, [Key(value)]);
            object workspace = GetWorkspace(contextType, context);
            object memo = workspace.GetType()
                .GetProperty("ValidatedRasterReferences", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(workspace)!;
            Assert.Equal(256, ((IEnumerable)memo).Cast<object>().Count());
            Assert.True((bool)contains.Invoke(context, [Key(0)])!);
            Assert.False((bool)contains.Invoke(context, [Key(256)])!);
            Assert.True(keyType.IsValueType);
            Assert.Equal(4, keyType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Length);
            Assert.All(keyType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic), field => Assert.Equal(typeof(ulong), field.FieldType));
        }
        finally
        {
            ((IDisposable)context).Dispose();
        }
    }

    [Fact]
    public void DisposedInspectionReturnsClearedWorkspaceAndStaleDisposeCannotClearNewBorrower()
    {
        Type contextType = typeof(SvgPreflight).GetNestedType("ResourceInspectionContext", BindingFlags.NonPublic)!;
        Type keyType = typeof(SvgPreflight).GetNestedType("RasterReferenceKey", BindingFlags.NonPublic)!;
        MethodInfo addResource = contextType.GetMethod("AddResource", BindingFlags.Instance | BindingFlags.Public)!;
        MethodInfo rememberRaster = contextType.GetMethod("RememberRaster", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo isRasterValidated = contextType.GetMethod("IsRasterValidated", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo markNested = contextType.GetMethod("MarkNestedSvgValidated", BindingFlags.Instance | BindingFlags.Public)!;
        MethodInfo isNestedValidated = contextType.GetMethod("IsNestedSvgValidatedAtDepth", BindingFlags.Instance | BindingFlags.Public)!;
        MethodInfo getScratch = contextType.GetMethod("GetAttributeScratch", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object key = Activator.CreateInstance(keyType, 42UL, 0UL, 0UL, 0UL)!;
        object? drain0 = null;
        object? drain1 = null;
        object? returned = null;
        object? borrower = null;
        try
        {
            // Drain both static slots so the context returned below is the next
            // one borrowed, independent of prior test order.
            drain0 = Activator.CreateInstance(contextType, nonPublic: true)!;
            drain1 = Activator.CreateInstance(contextType, nonPublic: true)!;
            returned = Activator.CreateInstance(contextType, nonPublic: true)!;
            Assert.True((bool)addResource.Invoke(returned, [key])!);
            rememberRaster.Invoke(returned, [key]);
            markNested.Invoke(returned, [key, 2]);
            object returnedWorkspace = GetWorkspace(contextType, returned);
            char[] returnedScratch = (char[])getScratch.Invoke(returned, [8 * 1024 * 1024 + 2])!;
            Assert.Equal(1026, returnedScratch.Length);
            returnedScratch.AsSpan().Fill('x');

            ((IDisposable)returned).Dispose();
            borrower = Activator.CreateInstance(contextType, nonPublic: true)!;
            Assert.Same(returnedWorkspace, GetWorkspace(contextType, borrower));
            char[] borrowedScratch = (char[])getScratch.Invoke(borrower, [8 * 1024 * 1024 + 2])!;
            Assert.Same(returnedScratch, borrowedScratch);
            Assert.Equal(-1, borrowedScratch.AsSpan().IndexOfAnyExcept('\0'));
            Assert.True((bool)addResource.Invoke(borrower, [key])!, "Returning a workspace must clear all prior resource identities.");
            Assert.False((bool)isRasterValidated.Invoke(borrower, [key])!, "Returning a workspace must clear raster memo entries.");
            Assert.False((bool)isNestedValidated.Invoke(borrower, [key, 1])!, "Returning a workspace must clear nested-SVG depth entries.");

            // A duplicate dispose from the former owner must not clear a
            // workspace that has since been reissued to another inspection.
            rememberRaster.Invoke(borrower, [key]);
            markNested.Invoke(borrower, [key, 2]);
            borrowedScratch[0] = 'y';
            ((IDisposable)returned).Dispose();
            Assert.Equal('y', borrowedScratch[0]);
            Assert.False((bool)addResource.Invoke(borrower, [key])!);
            Assert.True((bool)isRasterValidated.Invoke(borrower, [key])!);
            Assert.True((bool)isNestedValidated.Invoke(borrower, [key, 1])!);
            TargetInvocationException disposedFailure = Assert.Throws<TargetInvocationException>(() => addResource.Invoke(returned, [key]));
            Assert.IsType<ObjectDisposedException>(disposedFailure.InnerException);
        }
        finally
        {
            if (borrower is IDisposable borrowerDisposable)
                borrowerDisposable.Dispose();
            if (returned is IDisposable returnedDisposable)
                returnedDisposable.Dispose();
            if (drain0 is IDisposable drain0Disposable)
                drain0Disposable.Dispose();
            if (drain1 is IDisposable drain1Disposable)
                drain1Disposable.Dispose();
        }
    }

    [Fact]
    public void OversizedInspectionWorkspaceIsClearedAndDroppedInsteadOfRetained()
    {
        Type contextType = typeof(SvgPreflight).GetNestedType("ResourceInspectionContext", BindingFlags.NonPublic)!;
        Type keyType = typeof(SvgPreflight).GetNestedType("RasterReferenceKey", BindingFlags.NonPublic)!;
        MethodInfo addResource = contextType.GetMethod("AddResource", BindingFlags.Instance | BindingFlags.Public)!;
        MethodInfo rememberRaster = contextType.GetMethod("RememberRaster", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo markNested = contextType.GetMethod("MarkNestedSvgValidated", BindingFlags.Instance | BindingFlags.Public)!;
        object Key(ulong value) => Activator.CreateInstance(keyType, value, 0UL, 0UL, 0UL)!;
        object? drain0 = null;
        object? drain1 = null;
        object? oversized = null;
        object? borrower = null;
        try
        {
            // Drain both static slots without resetting shared production state.
            drain0 = Activator.CreateInstance(contextType, nonPublic: true)!;
            drain1 = Activator.CreateInstance(contextType, nonPublic: true)!;
            oversized = Activator.CreateInstance(contextType, nonPublic: true)!;
            for (ulong value = 0; value < 512; value++)
            {
                object key = Key(value);
                Assert.True((bool)addResource.Invoke(oversized, [key])!);
                markNested.Invoke(oversized, [key, 2]);
                rememberRaster.Invoke(oversized, [key]);
            }

            object oversizedWorkspace = GetWorkspace(contextType, oversized);
            long capacity = (long)oversizedWorkspace.GetType()
                .GetProperty("CombinedCollectionCapacity", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(oversizedWorkspace)!;
            Assert.True(capacity > 1024, $"The fixture must exceed the bounded pool capacity; observed {capacity}.");

            ((IDisposable)oversized).Dispose();
            object resources = oversizedWorkspace.GetType()
                .GetProperty("Resources", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(oversizedWorkspace)!;
            Assert.Empty(((IEnumerable)resources).Cast<object>());
            object nested = oversizedWorkspace.GetType()
                .GetProperty("DeepestValidatedNestedSvgDepth", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(oversizedWorkspace)!;
            Assert.Empty(((IEnumerable)nested).Cast<object>());
            object memo = oversizedWorkspace.GetType()
                .GetProperty("ValidatedRasterReferences", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(oversizedWorkspace)!;
            Assert.Empty(((IEnumerable)memo).Cast<object>());

            borrower = Activator.CreateInstance(contextType, nonPublic: true)!;
            Assert.NotSame(oversizedWorkspace, GetWorkspace(contextType, borrower));
            object firstKey = Key(0);
            Assert.True((bool)addResource.Invoke(borrower, [firstKey])!);
        }
        finally
        {
            if (borrower is IDisposable borrowerDisposable)
                borrowerDisposable.Dispose();
            if (oversized is IDisposable oversizedDisposable)
                oversizedDisposable.Dispose();
            if (drain0 is IDisposable drain0Disposable)
                drain0Disposable.Dispose();
            if (drain1 is IDisposable drain1Disposable)
                drain1Disposable.Dispose();
        }
    }

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
    private static object GetWorkspace(Type contextType, object context) =>
        contextType.GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
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
