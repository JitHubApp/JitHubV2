using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownSvgAdmissionTests
{
    [Fact]
    public void OpenedCarriesTheExactDocumentWeightAndAdmissionDomain()
    {
        using var document = new StubDocument();
        var domain = new object();
        MarkdownSvgAdmissionResult result = MarkdownSvgAdmissionResult.Opened(document, 731, domain);
        Assert.Equal(MarkdownSvgAdmissionStatus.Opened, result.Status);
        Assert.Same(document, result.Document);
        Assert.Equal(731, result.SourceReservationBytes);
        Assert.Same(domain, result.AdmissionDomain);
        Assert.Equal(0, result.RequiredSourceReservationBytes);
        Assert.Equal(0, result.CapacityVersion);
    }

    [Fact]
    public void CapacityDenialOwnsNoDocumentSourceOrDomain()
    {
        MarkdownSvgAdmissionResult result = MarkdownSvgAdmissionResult.CapacityUnavailable(19, 4096);
        Assert.Equal(MarkdownSvgAdmissionStatus.CapacityUnavailable, result.Status);
        Assert.Null(result.Document);
        Assert.Equal(0, result.SourceReservationBytes);
        Assert.Null(result.AdmissionDomain);
        Assert.Equal(19, result.CapacityVersion);
        Assert.Equal(4096, result.RequiredSourceReservationBytes);
    }

    [Fact]
    public void DefaultResultIsMalformedRatherThanAZeroWeightCapacityWait()
    {
        MarkdownSvgAdmissionResult result = default;
        Assert.Equal(MarkdownSvgAdmissionStatus.Invalid, result.Status);
        Assert.Null(result.Document);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void OpenedRequiresPositiveExactWeight(long bytes)
    {
        using var document = new StubDocument();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MarkdownSvgAdmissionResult.Opened(document, bytes, new object()));
    }

    [Fact]
    public void OpenedRequiresDocumentAndDomain()
    {
        using var document = new StubDocument();
        Assert.Throws<ArgumentNullException>(() => MarkdownSvgAdmissionResult.Opened(null!, 1, new object()));
        Assert.Throws<ArgumentNullException>(() => MarkdownSvgAdmissionResult.Opened(document, 1, null!));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, -1)]
    [InlineData(0, 0)]
    public void DenialRequiresValidVersionAndPositiveReservation(long version, long bytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MarkdownSvgAdmissionResult.CapacityUnavailable(version, bytes));

    private sealed class StubDocument : IMarkdownSvgDocument
    {
        public MarkdownSvgDocumentInfo Info { get; } = new();
        public ValueTask<MarkdownSvgRaster> RenderAsync(MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
