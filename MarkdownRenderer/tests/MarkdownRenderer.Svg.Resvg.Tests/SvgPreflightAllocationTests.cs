using System.Text;
using MarkdownRenderer.Svg.Resvg.Internal;
using MarkdownRenderer.Images;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgPreflightAllocationTests(ITestOutputHelper output)
{
    [Fact]
    public void DenseEmbeddedRasterPreflightKeepsTransientAllocationsBounded()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        string? fixturePath = null;
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "MarkdownRenderer",
                "tests",
                "MarkdownRenderer.Pixel.Tests", "Fixtures", "svg", "20-stress", "katex-182-unique-images.svg");
            if (File.Exists(candidate))
            {
                fixturePath = candidate;
                break;
            }
            directory = directory.Parent;
        }
        Assert.NotNull(fixturePath);
        byte[] source = File.ReadAllBytes(fixturePath);
        var options = new ResvgMarkdownSvgRendererOptions();
        InspectDenseAndRelease(source, options);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            InspectDenseAndRelease(source, options);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"Dense 182-image preflight: {allocated} allocated bytes / 100 inspections.");
        Assert.True(allocated < 100 * 80 * 1024,
            $"182-image preflight allocated {allocated} bytes for 100 inspections (budget <80 KiB each). " +
            "The private source clone and all synchronous validation work are included; no forced GC or retained memo is used.");
    }

    private static void InspectDenseAndRelease(byte[] source, ResvgMarkdownSvgRendererOptions options)
    {
        Task<SvgPreflightResult> inspection = SvgPreflight.InspectCoreAsync(source.ToArray(), options, CancellationToken.None);
        Assert.True(inspection.IsCompletedSuccessfully);
        SvgPreflightResult result = inspection.GetAwaiter().GetResult();
        try
        {
            Assert.Equal(182, result.EmbeddedPixels);
            Assert.Equal(890, result.Info.IntrinsicWidthDips);
            Assert.Equal(158, result.Info.IntrinsicHeightDips);
        }
        finally
        {
            result.ReleaseSource();
        }
    }

    [Theory]
    [InlineData("x", 0)]
    [InlineData("y", 63)]
    [InlineData("width", 64)]
    [InlineData("height", 129)]
    [InlineData("x", 1023)]
    [InlineData("y", 1024)]
    [InlineData("width", 1025)]
    [InlineData("height", 4097)]
    public async Task AttributeChunksStillRejectHiddenExternalCss(string attribute, int padding)
    {
        string value = new string(' ', padding) + @"u\72l(https://example.test/forbidden.svg)";
        byte[] source = Encoding.UTF8.GetBytes(
            $"<svg xmlns='http://www.w3.org/2000/svg'><rect {attribute}='{value}'/></svg>");
        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(() =>
            SvgPreflight.InspectCoreAsync(source, new(), CancellationToken.None));
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(256)]
    public async Task AttributeChunksPreserveStructuralCharging(int length)
    {
        string value = "  " + new string('1', length) + "  ";
        byte[] source = Encoding.UTF8.GetBytes(
            $"<svg xmlns='http://www.w3.org/2000/svg'><rect x='{value}'/></svg>");
        SvgPreflightResult result = await SvgPreflight.InspectCoreAsync(source, new(), CancellationToken.None);
        try
        {
            Assert.Equal(2 + length / 64, result.StructuralCost);
        }
        finally
        {
            result.ReleaseSource();
        }
    }

    [Theory]
    [InlineData(1022)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(1026)]
    [InlineData(2051)]
    [InlineData(2049)]
    public async Task AttributeChunksPreserveDecodedSurrogatePairs(int prefixLength)
    {
        string prefix = new('a', prefixLength);
        string decoded = prefix + "\U0001F642" + new string('b', 1023);
        byte[] source = Encoding.UTF8.GetBytes(
            $"<svg xmlns='http://www.w3.org/2000/svg'><rect aria-label='{prefix}&#x1F642;{new string('b', 1023)}'/></svg>");
        SvgPreflightResult result = await SvgPreflight.InspectCoreAsync(source, new(), CancellationToken.None);
        try
        {
            Assert.Equal(2 + Encoding.UTF8.GetByteCount(decoded) / 64, result.StructuralCost);
        }
        finally
        {
            result.ReleaseSource();
        }
    }

    [Fact]
    public void SmallUtf8SourcesDoNotReserveAsyncIoBuffersPerInspection()
    {
        byte[] source = Encoding.UTF8.GetBytes("""
            <svg xmlns="http://www.w3.org/2000/svg" width="128" height="64">
            <desc>Small badge with RTL text</desc>
            <style>text { fill: currentColor; }</style>
            <text x="120" y="52" direction="rtl">שלום</text>
            </svg>
            """);
        var options = new ResvgMarkdownSvgRendererOptions();
        InspectAndRelease(source, options);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            InspectAndRelease(source, options);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"Small text SVG preflight: {allocated} allocated bytes / 100 inspections.");
        Assert.True(allocated < 100 * 32 * 1024,
            $"Small in-memory preflight allocated {allocated} bytes for 100 inspections (budget <32 KiB each).");
    }

    [Theory]
    [InlineData("d", 64)]
    [InlineData("d", 1026)]
    [InlineData("d", 2052)]
    [InlineData("d", 1024 * 1024)]
    [InlineData("D", 1026)]
    [InlineData("f:d", 1026)]
    public async Task PathChunkChargingPreservesQualifiedNameAndUntrimmedPayload(string attribute, int padding)
    {
        string value = new string(' ', padding) + "M0 0";
        byte[] source = Encoding.UTF8.GetBytes(
            $"<svg xmlns='http://www.w3.org/2000/svg' xmlns:f='urn:test'><path {attribute}='{value}'/></svg>");
        SvgPreflightResult result = await SvgPreflight.InspectCoreAsync(source, new(), CancellationToken.None);
        try
        {
            // The document/path elements cost two. Only exact unqualified d
            // has path cost: ceil(commands/8), ceil(numbers/16) and ceil(payload/64).
            Assert.Equal(attribute == "d" ? 2 + 2 + (value.Length + 63) / 64 : 2, result.StructuralCost);
        }
        finally
        {
            result.ReleaseSource();
        }
    }

    private static void InspectAndRelease(byte[] source, ResvgMarkdownSvgRendererOptions options)
    {
        Task<SvgPreflightResult> inspection = SvgPreflight.InspectCoreAsync(source.ToArray(), options, CancellationToken.None);
        // UTF-8 has no normalization await. Keeping this assertion makes the
        // thread-local allocation measurement account for all inspection work.
        Assert.True(inspection.IsCompletedSuccessfully);
        SvgPreflightResult result = inspection.GetAwaiter().GetResult();
        try
        {
            Assert.True(result.Info.HasText);
            Assert.True(result.Info.UsesCurrentColor);
            Assert.Equal("Small badge with RTL text", result.Info.Description);
        }
        finally
        {
            result.ReleaseSource();
        }
    }
}
