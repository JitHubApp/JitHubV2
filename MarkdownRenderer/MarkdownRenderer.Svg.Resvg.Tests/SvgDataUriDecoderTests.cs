using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgDataUriDecoderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(31)]
    public void Base64MatchesPlatformDecoderWithPaddingAndWhitespace(int length)
    {
        byte[] bytes = Enumerable.Range(0, length).Select(index => (byte)index).ToArray();
        string value = " \t" + string.Join("\r\n", Convert.ToBase64String(bytes).Select(c => c.ToString())) + " \n";

        byte[] decoded = SvgDataUriDecoder.DecodeBase64(value, length, CancellationToken.None);
        int boundedLength = SvgDataUriDecoder.GetBase64DecodedLength(value, length, CancellationToken.None);
        Span<byte> scratch = stackalloc byte[31];
        SvgDataUriDecoder.DecodeBase64Into(value, scratch[..boundedLength], CancellationToken.None);

        Assert.Equal(Convert.FromBase64String(value), decoded);
        Assert.Equal(length, decoded.Length);
        Assert.True(decoded.AsSpan().SequenceEqual(scratch[..boundedLength]));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("A===")]
    [InlineData("=AAA")]
    [InlineData("AA=A")]
    [InlineData("AA==A===")]
    [InlineData("AA==\u000b")]
    [InlineData("AA==\u000c")]
    [InlineData("AA==\u00a0")]
    [InlineData("A_==")]
    public void MalformedBase64RemainsRejected(string value)
    {
        Assert.Throws<FormatException>(() => Convert.FromBase64String(value));
        Assert.Throws<FormatException>(() => SvgDataUriDecoder.DecodeBase64(value, 1024, CancellationToken.None));
        Assert.Throws<FormatException>(() => SvgDataUriDecoder.GetBase64DecodedLength(value, 1024, CancellationToken.None));
    }

    [Fact]
    public void SmallBase64InspectionUsesNoManagedPayloadAllocation()
    {
        const string value = " AAEC AwQF Bg== \r\n";
        Span<byte> scratch = stackalloc byte[512];
        int length = SvgDataUriDecoder.GetBase64DecodedLength(value, scratch.Length, CancellationToken.None);
        SvgDataUriDecoder.DecodeBase64Into(value, scratch[..length], CancellationToken.None);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            length = SvgDataUriDecoder.GetBase64DecodedLength(value, scratch.Length, CancellationToken.None);
            SvgDataUriDecoder.DecodeBase64Into(value, scratch[..length], CancellationToken.None);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(7, length);
        Assert.Equal(0, allocated);
        Assert.Equal(6, scratch[6]);
    }

    [Fact]
    public void CallerOwnedDecodeRejectsWrongStorageLengthAndCancellation()
    {
        Assert.Throws<FormatException>(() => SvgDataUriDecoder.DecodeBase64Into("AQID", new byte[2], CancellationToken.None));
        Assert.Throws<FormatException>(() => SvgDataUriDecoder.DecodeBase64Into("AQID", new byte[4], CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SvgDataUriDecoder.DecodeBase64Into("AQID", new byte[3], cancellation.Token));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("a+b%20c", "a+b c")]
    [InlineData("%00%ff%aB", "")]
    [InlineData("👩🏽‍💻 café العربية", "👩🏽‍💻 café العربية")]
    [InlineData("👩%F0%9F%8F%BD‍💻%20café", "👩🏽‍💻 café")]
    [InlineData("é%20中文", "é 中文")]
    public void PercentDecodingPreservesUtf8ScalarsAndDoesNotFormDecodePlus(string value, string expected)
    {
        byte[] decoded = SvgDataUriDecoder.DecodePercentEncoded(value, 1024, CancellationToken.None);
        if (value == "%00%ff%aB")
            Assert.Equal(new byte[] { 0, 255, 171 }, decoded);
        else
            Assert.Equal(Encoding.UTF8.GetBytes(expected), decoded);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("%0")]
    [InlineData("%GG")]
    [InlineData("ok%F")]
    [InlineData("%ＦＦ")]
    public void MalformedPercentEncodingRemainsRejected(string value)
    {
        Assert.Throws<FormatException>(() => SvgDataUriDecoder.DecodePercentEncoded(value, 1024, CancellationToken.None));
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void IsolatedSurrogateUsesThePlatformUtf8ReplacementFallback(int codeUnit)
    {
        // Construct at runtime: custom-attribute strings are UTF-8 encoded and
        // cannot faithfully carry an isolated UTF-16 surrogate as test input.
        string value = new((char)codeUnit, 1);
        byte[] decoded = SvgDataUriDecoder.DecodePercentEncoded(value, 3, CancellationToken.None);
        Assert.Equal(Encoding.UTF8.GetBytes(value), decoded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodingAllocatesOnlyTheExactPayloadArray(bool base64)
    {
        const int length = 256 * 1024;
        string value = base64
            ? Convert.ToBase64String(new byte[length])
            : string.Concat(Enumerable.Repeat("%00", length));
        _ = Decode(value, base64, length, CancellationToken.None);
        long before = GC.GetAllocatedBytesForCurrentThread();

        byte[] decoded = Decode(value, base64, length, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(length, decoded.Length);
        Assert.InRange(allocated, length, length + 4096);
        GC.KeepAlive(decoded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverBudgetPayloadIsRejectedBeforeItsArrayIsAllocated(bool base64)
    {
        const int length = 256 * 1024;
        string value = base64 ? Convert.ToBase64String(new byte[length]) : new string('x', length);
        Assert.Throws<MarkdownSvgException>(() => Decode(value, base64, length - 1, CancellationToken.None));
        long before = GC.GetAllocatedBytesForCurrentThread();
        MarkdownSvgException? failure = null;
        try
        {
            _ = Decode(value, base64, length - 1, CancellationToken.None);
        }
        catch (MarkdownSvgException exception)
        {
            failure = exception;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(failure);
        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);
        Assert.True(allocated < 64 * 1024, $"Rejected {length}-byte payload allocated {allocated} bytes.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationIsObservedBeforePayloadAllocation(bool base64)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Decode("AAAA", base64, 1024, cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreflightDeduplicatesEquivalentNestedUtf8Payloads(bool base64)
    {
        const string nested = "<svg xmlns='http://www.w3.org/2000/svg'><text>👩🏽‍💻 é العربية</text></svg>";
        byte[] bytes = Encoding.UTF8.GetBytes(nested);
        string percent = nested.Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal);
        string otherPayload = base64 ? Convert.ToBase64String(bytes) : percent;
        string otherHeader = base64 ? ";base64" : string.Empty;
        string source = "<svg xmlns='http://www.w3.org/2000/svg'>" +
            $"<image href=\"data:image/svg+xml,{percent}\"/>" +
            $"<image href=\"data:image/svg+xml{otherHeader},{otherPayload}\"/></svg>";

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(source)),
            new ResvgMarkdownSvgRendererOptions(), CancellationToken.None);

        Assert.Equal(bytes.Length, result.EmbeddedBytes);
    }

    [Fact]
    public async Task PreflightHonorsLoweredEmbeddedByteCeilingForPercentPayload()
    {
        string source = "<svg xmlns='http://www.w3.org/2000/svg'><image href='data:image/svg+xml,%3Csvg/%3E'/></svg>";
        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(source)),
                new ResvgMarkdownSvgRendererOptions(maxEmbeddedImageBytes: 5), CancellationToken.None));
        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);
    }

    private static byte[] Decode(string value, bool base64, long maximumBytes, CancellationToken cancellationToken) =>
        base64
            ? SvgDataUriDecoder.DecodeBase64(value, maximumBytes, cancellationToken)
            : SvgDataUriDecoder.DecodePercentEncoded(value, maximumBytes, cancellationToken);
}
