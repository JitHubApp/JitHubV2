using System;
using System.Reflection;
using System.Text;
using System.Threading;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SvgPreparedMetadataCustodyTests
{
    [Fact]
    public void PreparedMetadataBelongsOnlyToTheExactAdmittedArray()
    {
        byte[] source = Source("original", 48, 24);
        SvgSourceMetadata metadata = SvgSourceMetadata.Extract(source, CancellationToken.None);
        MarkdownSvgSourcePreparation preparation = MarkdownSvgSourcePreparation.AdmitWithMetadata(source, metadata);

        Assert.Same(source, preparation.SanitizedBytes);
        Assert.True(preparation.TryGetPreparedMetadata(source, out SvgSourceMetadata matching));
        Assert.Equal(metadata, matching);
        byte[] copy = (byte[])source.Clone();
        Assert.Equal(source, copy);
        Assert.False(preparation.TryGetPreparedMetadata(copy, out SvgSourceMetadata copied));
        Assert.Equal(default, copied);
    }

    [Fact]
    public void PublicAdmissionAndRejectionCannotSupplyInternalPreparedMetadata()
    {
        byte[] source = Source("fallback", 48, 24);
        MarkdownSvgSourcePreparation generic = MarkdownSvgSourcePreparation.Admit(source);
        MarkdownSvgSourcePreparation rejected = MarkdownSvgSourcePreparation.Reject(
            MarkdownSvgFailureReason.UnsupportedContent, "unsupported fixture");

        Assert.False(generic.TryGetPreparedMetadata(source, out SvgSourceMetadata absent));
        Assert.Equal(default, absent);
        Assert.False(rejected.TryGetPreparedMetadata(source, out SvgSourceMetadata rejectedMetadata));
        Assert.Equal(default, rejectedMetadata);
        Assert.Null(rejected.SanitizedBytes);
        Assert.Equal("fallback", SvgSourceMetadata.Extract(source, CancellationToken.None).Title);
    }

    [Fact]
    public void PreparedMetadataHelpersRejectNullSource()
    {
        Assert.Throws<ArgumentNullException>(() => MarkdownSvgSourcePreparation.AdmitWithMetadata(null!, default));
        MarkdownSvgSourcePreparation preparation = MarkdownSvgSourcePreparation.Admit(Source("label", 1, 1));
        Assert.Throws<ArgumentNullException>(() => preparation.TryGetPreparedMetadata(null!, out _));
    }

    [Fact]
    public void BorrowedMetadataStreamChecksCancellationAtEveryReadEntryPoint()
    {
        byte[] source = Source("unchanged", 48, 24);
        byte[] original = (byte[])source.Clone();
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancellationAwareMemoryStream(source, cancellation.Token);
        Assert.Equal(source[0], stream.ReadByte());
        cancellation.Cancel();
        byte[] buffer = new byte[8];

        Assert.ThrowsAny<OperationCanceledException>(() => stream.ReadByte());
        Assert.ThrowsAny<OperationCanceledException>(() => stream.Read(buffer, 0, buffer.Length));
        Assert.ThrowsAny<OperationCanceledException>(() => stream.Read(buffer.AsSpan()));
        Assert.ThrowsAny<OperationCanceledException>(() => { _ = stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None); });
        Assert.ThrowsAny<OperationCanceledException>(() => { _ = stream.ReadAsync(buffer.AsMemory(), CancellationToken.None); });
        Assert.Equal(original, source);
    }

    [Fact]
    public void ImagePreflightUsesCarriedMetadataOnlyForItsPreparedByteIdentity()
    {
        byte[] admittedSource = Source("XML label", 48, 24);
        SvgSourceMetadata carried = SvgSourceMetadata.Extract(Source("carried label", 96, 32), CancellationToken.None);
        MarkdownSvgSourcePreparation preparation = MarkdownSvgSourcePreparation.AdmitWithMetadata(admittedSource, carried);

        var exact = Preflight(admittedSource, preparation);
        Assert.Equal(new Size(96, 32), exact.Intrinsic);
        Assert.Equal("carried label", exact.Title);

        byte[] copiedSource = (byte[])admittedSource.Clone();
        var copied = Preflight(copiedSource, preparation);
        Assert.Equal(new Size(48, 24), copied.Intrinsic);
        Assert.Equal("XML label", copied.Title);
        Assert.Equal(exact.ContentHash, copied.ContentHash);
        Assert.Equal(exact.PartitionHash, copied.PartitionHash);
    }

    [Fact]
    public void ImagePreflightKeepsGenericProviderFallbackAndRejectedSourceContract()
    {
        byte[] source = Source("generic provider", 48, 24);
        var generic = Preflight(source, MarkdownSvgSourcePreparation.Admit(source));
        Assert.Equal(new Size(48, 24), generic.Intrinsic);
        Assert.Equal("generic provider", generic.Title);

        var rejected = Preflight(source, MarkdownSvgSourcePreparation.Reject(
            MarkdownSvgFailureReason.UnsupportedContent, "unsupported fixture"));
        Assert.Equal(default, rejected.Intrinsic);
        Assert.Null(rejected.Title);
        Assert.Null(rejected.Desc);
        Assert.Equal(generic.ContentHash, rejected.ContentHash);
        Assert.Equal(generic.PartitionHash, rejected.PartitionHash);
    }

    private static (Size Intrinsic, string? Title, string? Desc, string ContentHash, string PartitionHash) Preflight(
        byte[] source, MarkdownSvgSourcePreparation preparation)
    {
        MethodInfo method = typeof(ImageBox).GetMethod("PreflightSvg", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Image preflight entry point is missing.");
        return ((Size, string?, string?, string, string))method.Invoke(
            null, [source, preparation, default(Size), "metadata-custody-fixture", CancellationToken.None])!;
    }

    private static byte[] Source(string title, int width, int height) => Encoding.UTF8.GetBytes(
        $"<svg xmlns='http://www.w3.org/2000/svg' width='{width}' height='{height}'><title>{title}</title></svg>");
}
