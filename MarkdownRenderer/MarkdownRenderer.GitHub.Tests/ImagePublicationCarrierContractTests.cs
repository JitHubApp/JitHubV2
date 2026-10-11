using System.Reflection;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ImagePublicationCarrierContractTests
{
    [Fact]
    public void SvgPublicationFactoryAcceptsOnlyWeakSourceIdentityAndScalarLength()
    {
        MethodInfo factory = typeof(ImageBox).GetMethod(
            "CreateSvgPublicationMetadata", BindingFlags.Static | BindingFlags.NonPublic)!;
        ParameterInfo[] parameters = factory.GetParameters();
        Assert.Equal(typeof(WeakReference<byte[]>), parameters[0].ParameterType);
        Assert.Equal(typeof(long), parameters[1].ParameterType);
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(byte[]));
        FieldInfo[] fields = factory.ReturnType.GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(byte[]));
        Assert.Equal(typeof(WeakReference<byte[]>),
            Assert.Single(fields, field => field.Name == "SourceIdentity").FieldType);
    }

    [Fact]
    public void PreparedSourceAliasesAreClearedBeforeLeaseReleaseAndRenderWait_SourceContract()
    {
        string source = ReadImageSource();
        int start = source.IndexOf("private async Task<SvgOpenStageResult> OpenSvgSourceCoreAsync(",
            StringComparison.Ordinal);
        int end = source.IndexOf("private Task RenderOpenedSvgAsync(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string core = source[start..end];
        int handoff = core.IndexOf("var publicationSourceIdentity = new WeakReference<byte[]>(rawBytes);",
            StringComparison.Ordinal);
        int ownerTransfer = core.IndexOf("return new SvgOpenStageResult(work, null);", StringComparison.Ordinal);
        Assert.True(handoff >= 0 && ownerTransfer > handoff);
        string transition = core[handoff..ownerTransfer];
        int release = transition.IndexOf("hostPreparationLease?.Dispose();", StringComparison.Ordinal);
        Assert.True(release >= 0);
        foreach (string clear in new[] { "sourceEntry = null;", "preparation = null;", "openRequest = null;",
                     "rawBytes = Array.Empty<byte>();" })
        {
            int clearIndex = transition.IndexOf(clear, StringComparison.Ordinal);
            Assert.True(clearIndex >= 0 && clearIndex < release, $"'{clear}' must precede byte-lease release.");
        }
        int renderStart = source.IndexOf("private async Task RenderOpenedSvgCoreAsync(", end, StringComparison.Ordinal);
        int renderEnd = source.IndexOf("private void HandleSvgWorkFailure(", renderStart, StringComparison.Ordinal);
        Assert.True(renderStart >= 0 && renderEnd > renderStart);
        string render = source[renderStart..renderEnd];
        Assert.Contains("await renderGate.WaitAsync(cancellationToken)", render, StringComparison.Ordinal);
        Assert.DoesNotContain("rawBytes", render, StringComparison.Ordinal);
        Assert.DoesNotContain("SvgCacheEntry", render, StringComparison.Ordinal);
        Assert.Contains("UpdateSvgSourceCacheBitmap(\n                work.BuiltInSourceAliasKey,\n                contentCacheKey,", render,
            StringComparison.Ordinal);
        Assert.DoesNotContain("new WeakReference<SvgCacheEntry>", core, StringComparison.Ordinal);
        Assert.Contains("_svgCache.TryGetValue(contentCacheKey, out SvgCacheEntry? sourceEntry)", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("sourceEntry.WithLastBitmap(", render, StringComparison.Ordinal);
        string cleanup = core[core.LastIndexOf("finally\n        {", StringComparison.Ordinal)..];
        Assert.Contains("sourceEntry = null;", cleanup, StringComparison.Ordinal);
        Assert.Contains("openRequest = null;", cleanup, StringComparison.Ordinal);
        Assert.Contains("preparation = null;", cleanup, StringComparison.Ordinal);
        Assert.Contains("rawBytes = Array.Empty<byte>();", cleanup, StringComparison.Ordinal);
        Assert.Contains("TryImageCleanup(hostPreparationLease.Dispose)", cleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenedRenderHandoffContainsNoSourceBearingPayloadFields()
    {
        Type work = typeof(ImageBox).GetNestedType("SvgOpenedRenderWork", BindingFlags.NonPublic)!;
        FieldInfo[] fields = work.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(byte[]) ||
            field.FieldType == typeof(Memory<byte>) || field.FieldType == typeof(ReadOnlyMemory<byte>) ||
            field.FieldType.Name is "MarkdownSvgSourcePreparation" or "MarkdownSvgOpenRequest" or
                "SvgCacheEntry" or "MarkdownImageAsset" or "MarkdownImageResolution" or
                "IMarkdownImageResolutionLease" or "MarkdownBuiltInImageSource");
        Assert.Contains(fields, field => field.Name == "Document" &&
            field.FieldType.Name == "IMarkdownSvgDocument");
        Assert.Contains(fields, field => field.Name == "ContentCacheKey" && field.IsInitOnly);
        Assert.Contains(fields, field => field.Name == "Metadata" && field.IsInitOnly);
    }

    [Fact]
    public void PendingRenderDisposalPreventsConsumptionAndRetiresOnce()
    {
        ImageBox.SvgOpenedRenderOwnership ownership = default;
        Assert.False(ownership.TryCompleteConsumed());
        Assert.True(ownership.TryDisposePending());
        Assert.False(ownership.TryDisposePending());
        Assert.False(ownership.TryConsume());
        Assert.False(ownership.TryCompleteConsumed());
    }

    [Fact]
    public void ConsumedRenderCannotBeDisposedBeforeActualCompletion()
    {
        ImageBox.SvgOpenedRenderOwnership ownership = default;
        Assert.True(ownership.TryConsume());
        Assert.False(ownership.TryConsume());
        Assert.False(ownership.TryDisposePending());
        Assert.True(ownership.TryCompleteConsumed());
        Assert.False(ownership.TryCompleteConsumed());
        Assert.False(ownership.TryDisposePending());
        Assert.False(ownership.TryConsume());
    }

    [Theory]
    [InlineData("CreateTiledSvgPublication")]
    [InlineData("CreateBitmapSvgPublication")]
    public void SvgPublicationReturnsAnAllocationFreeCallbackPair(string methodName)
    {
        Type carrier = typeof(ImageBox).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!.ReturnType;
        Assert.True(carrier.IsValueType);
        Assert.Equal(typeof(ValueTuple<Action, Action>), carrier);
        FieldInfo[] fields = carrier.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.Equal(2, fields.Length);
        Assert.All(fields, field =>
        {
            Assert.Equal(typeof(Action), field.FieldType);
        });
    }

    private static string ReadImageSource()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer",
                "Layout", "Boxes", "ImageBox.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        throw new FileNotFoundException("Could not locate the ImageBox source from test output.");
    }
}
