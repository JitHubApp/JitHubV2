using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgUtf8TextReaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4095)]
    [InlineData(4096)]
    public void DirectDecodePreservesEveryUtf16CodeUnit(int chunkSize)
    {
        string expected = new('x', 4094);
        expected += "🙂שלוםe\u0301中文" + new string('y', 4095) + "🚀";
        byte[] source = Encoding.UTF8.GetBytes("BOM" + expected);
        using var reader = CreateReader(source, 3);
        var destination = new char[chunkSize];
        var actual = new StringBuilder();
        int count;
        while ((count = reader.Read(destination, 0, destination.Length)) > 0)
            actual.Append(destination, 0, count);
        Assert.Equal(expected, actual.ToString());
        Assert.Equal(-1, reader.Read());
    }

    [Fact]
    public void EmptyReadDoesNotConsumePendingSurrogateAndDisposeClosesReader()
    {
        var reader = CreateReader(Encoding.UTF8.GetBytes("🙂z"), 0);
        Assert.Equal((int)'\ud83d', reader.Read());
        Assert.Equal(0, reader.Read(Span<char>.Empty));
        Assert.Equal((int)'\ude42', reader.Read());
        Assert.Equal((int)'z', reader.Read());
        Assert.Equal(-1, reader.Read());
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.Read());
    }

    [Theory]
    [InlineData("C080")]
    [InlineData("EDA080")]
    [InlineData("F4908080")]
    [InlineData("E282")]
    [InlineData("80")]
    public void DirectDecodeRejectsInvalidUtf8(string invalidHex)
    {
        using var reader = CreateReader(Convert.FromHexString(invalidHex), 0);
        Assert.Throws<DecoderFallbackException>(() => reader.ReadToEnd());
    }

    [Fact]
    public void CancellationIsObservedBetweenParserBufferReads()
    {
        using var cancellation = new CancellationTokenSource();
        using var reader = CreateReader(new byte[8192], 0, cancellation.Token);
        var destination = new char[4096];
        Assert.Equal(destination.Length, reader.Read(destination, 0, destination.Length));
        cancellation.Cancel();
        OperationCanceledException failure = Assert.Throws<OperationCanceledException>(() =>
            reader.Read(destination, 0, destination.Length));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf8-declared")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf16-le-no-bom")]
    [InlineData("utf16-be-no-bom")]
    [InlineData("utf32-le")]
    [InlineData("utf32-be")]
    [InlineData("latin1")]
    public async Task LargeSourcePreservesEncodingDetectionAndMetadata(string kind)
    {
        string description = kind == "latin1" ? "café" : "🙂שלוםe\u0301中文";
        string declaration = kind switch
        {
            "utf8-declared" => "<?xml version='1.0' encoding='utf-8'?>",
            "latin1" => "<?xml version='1.0' encoding='iso-8859-1'?>",
            _ => "",
        };
        Encoding encoding = kind switch
        {
            "utf8-bom" => new UTF8Encoding(true),
            "utf16-le" => new UnicodeEncoding(false, true),
            "utf16-be" => new UnicodeEncoding(true, true),
            "utf16-le-no-bom" => new UnicodeEncoding(false, false),
            "utf16-be-no-bom" => new UnicodeEncoding(true, false),
            "utf32-le" => new UTF32Encoding(false, true),
            "utf32-be" => new UTF32Encoding(true, true),
            "latin1" => Encoding.Latin1,
            _ => new UTF8Encoding(false),
        };
        string xml = declaration + "<svg xmlns='http://www.w3.org/2000/svg' width='7' height='5'>" +
            "<!--" + new string('x', 8192) + "--><desc>" + description + "</desc></svg>";
        byte[] source = [.. encoding.GetPreamble(), .. encoding.GetBytes(xml)];
        SvgPreflightResult result = await SvgPreflight.InspectCoreAsync(source, new(), CancellationToken.None);
        try
        {
            Assert.Equal(description, result.Info.Description);
            Assert.Equal(7, result.Info.IntrinsicWidthDips);
            Assert.Equal(5, result.Info.IntrinsicHeightDips);
            Assert.Contains(description, Encoding.UTF8.GetString(result.Source.CopyToArrayForTest()));
        }
        finally
        {
            result.ReleaseSource();
        }
    }

    [Theory]
    [InlineData("<script/>")]
    [InlineData("<image href='https://example.test/image.png'/>")]
    [InlineData("<rect onclick='alert(1)'/>")]
    [InlineData("<style>@import url(https://example.test/style.css);</style>")]
    public async Task DirectDecodeDoesNotBypassSecurityChecks(string forbidden)
    {
        byte[] source = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'>" +
            "<!--" + new string('x', 8192) + "-->" + forbidden + "</svg>");
        MarkdownSvgException error = await Assert.ThrowsAsync<MarkdownSvgException>(() =>
            SvgPreflight.InspectCoreAsync(source, new(), CancellationToken.None));
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, error.Reason);
    }

    [Fact]
    public async Task InvalidLargeUtf8ReturnsTypedFailure()
    {
        byte[] source = [.. Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><desc>" +
            new string('x', 8192)), 0xed, 0xa0, 0x80, .. "</desc></svg>"u8];
        MarkdownSvgException error = await Assert.ThrowsAsync<MarkdownSvgException>(() =>
            SvgPreflight.InspectCoreAsync(source, new(), CancellationToken.None));
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, error.Reason);
    }

    [Fact]
    public void DecoderCarriesUtf8CodePointAcrossSegmentBoundaryAndOwnerRetirement()
    {
        string expected = new string('x', SvgSourceBuffer.SegmentSize - 1) + "🙂tail";
        byte[] bytes = Encoding.UTF8.GetBytes(expected);
        using var output = new SvgPreflight.BoundedOutputStream(bytes.Length, CancellationToken.None);
        output.Write(bytes);
        SvgSourceBuffer owner = output.DetachSourceBuffer();
        var reader = new SvgUtf8TextReader(owner.AcquireReadLease(), 0);
        owner.Dispose();

        using (reader)
        {
            Assert.Equal(expected, reader.ReadToEnd());
        }
    }

    private static SvgUtf8TextReader CreateReader(
        byte[] source,
        int offset,
        CancellationToken cancellationToken = default)
    {
        using SvgSourceBuffer owner = SvgSourceBuffer.CopyFrom(source);
        return new SvgUtf8TextReader(owner.AcquireReadLease(), offset, cancellationToken);
    }
}
