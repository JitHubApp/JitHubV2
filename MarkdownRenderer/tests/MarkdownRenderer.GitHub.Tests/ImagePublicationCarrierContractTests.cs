using System.Reflection;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ImagePublicationCarrierContractTests
{
    [Fact]
    public void SvgPublicationFactoryAcceptsOnlyWeakSourceIdentityAndScalarLength()
    {
        ConstructorInfo factory = Assert.Single(typeof(SvgPublicationMetadata).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        ParameterInfo[] parameters = factory.GetParameters();
        Assert.Equal(typeof(WeakReference<byte[]>), parameters[0].ParameterType);
        Assert.Equal(typeof(long), parameters[1].ParameterType);
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(byte[]));
        FieldInfo[] fields = typeof(SvgPublicationMetadata).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(byte[]));
        Assert.Equal(typeof(WeakReference<byte[]>),
            Assert.Single(fields, field => field.Name == "SourceIdentity").FieldType);
    }

    [Fact]
    public void OpenedRenderHandoffContainsNoSourceBearingPayloadFields()
    {
        Type work = typeof(SvgOpenedRenderWork);
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
        SvgOpenedRenderOwnership ownership = default;
        Assert.False(ownership.TryCompleteConsumed());
        Assert.True(ownership.TryDisposePending());
        Assert.False(ownership.TryDisposePending());
        Assert.False(ownership.TryConsume());
        Assert.False(ownership.TryCompleteConsumed());
    }

    [Fact]
    public void ConsumedRenderCannotBeDisposedBeforeActualCompletion()
    {
        SvgOpenedRenderOwnership ownership = default;
        Assert.True(ownership.TryConsume());
        Assert.False(ownership.TryConsume());
        Assert.False(ownership.TryDisposePending());
        Assert.True(ownership.TryCompleteConsumed());
        Assert.False(ownership.TryCompleteConsumed());
        Assert.False(ownership.TryDisposePending());
        Assert.False(ownership.TryConsume());
    }

    [Theory]
    [InlineData(nameof(SvgPublicationCommit.CreateTiledPublication))]
    [InlineData(nameof(SvgPublicationCommit.CreateBitmapPublication))]
    public void SvgPublicationReturnsAnAllocationFreeCallbackPair(string methodName)
    {
        Type carrier = typeof(SvgPublicationCommit).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!.ReturnType;
        Assert.True(carrier.IsValueType);
        Assert.Equal(typeof(ValueTuple<Action, Action>), carrier);
        FieldInfo[] fields = carrier.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.Equal(2, fields.Length);
        Assert.All(fields, field =>
        {
            Assert.Equal(typeof(Action), field.FieldType);
        });
    }
}
