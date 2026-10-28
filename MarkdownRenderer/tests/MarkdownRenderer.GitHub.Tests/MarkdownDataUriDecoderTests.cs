using System;
using System.IO;
using System.Text;
using System.Threading;
using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownDataUriDecoderTests
{
    [Fact]
    public void DecodesUnescapedBase64DirectlyWithinExactByteBudget()
    {
        byte[] decoded = MarkdownDataUriDecoder.Decode(
            "DATA:image/png;charset=binary;BASE64,AQIDBA==",
            maximumDecodedBytes: 4);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, decoded);
    }

    [Fact]
    public void DecodesBase64AsciiWhitespaceAndPercentEscapesWithoutPayloadCopy()
    {
        byte[] whitespace = MarkdownDataUriDecoder.Decode(
            "data:image/png;base64, AQI=\r\n\v\f",
            maximumDecodedBytes: 2);
        byte[] percentEscaped = MarkdownDataUriDecoder.Decode(
            "data:application/octet-stream;base64,%41%51%49%44",
            maximumDecodedBytes: 3);

        Assert.Equal(new byte[] { 1, 2 }, whitespace);
        Assert.Equal(new byte[] { 1, 2, 3 }, percentEscaped);
    }

    [Fact]
    public void PercentPayloadPreservesArbitraryBytesAndDoesNotTreatPlusAsSpace()
    {
        byte[] decoded = MarkdownDataUriDecoder.Decode(
            "data:application/octet-stream,%00%89%FF+A",
            maximumDecodedBytes: 5);

        Assert.Equal(new byte[] { 0x00, 0x89, 0xFF, (byte)'+', (byte)'A' }, decoded);
    }

    [Fact]
    public void PercentPayloadEncodesLiteralUnicodeAsUtf8()
    {
        byte[] decoded = MarkdownDataUriDecoder.Decode(
            "data:image/svg+xml;utf8,%3Csvg%3Eπ😀</svg>",
            maximumDecodedBytes: 32);

        Assert.Equal(Encoding.UTF8.GetBytes("<svg>π😀</svg>"), decoded);
    }

    [Fact]
    public void RejectsDecodedPayloadsOverTheCallerBudgetBeforeAllocation()
    {
        Assert.Throws<InvalidDataException>(() =>
            MarkdownDataUriDecoder.Decode("data:;base64,AQID", maximumDecodedBytes: 2));
        Assert.Throws<InvalidDataException>(() =>
            MarkdownDataUriDecoder.Decode("data:,%00%01", maximumDecodedBytes: 1));
        Assert.Throws<InvalidDataException>(() =>
            MarkdownDataUriDecoder.Decode("data:;base64,AQID", maximumDecodedBytes: 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.test/image.png")]
    [InlineData("data:image/png")]
    [InlineData("data:,")]
    [InlineData("data:image/png,%")]
    [InlineData("data:image/png,%G0")]
    [InlineData("data:;base64,QQ=Z")]
    [InlineData("data:;base64,AAAA=AAA")]
    public void RejectsMalformedOrEmptyDataUris(string value)
    {
        Assert.Throws<InvalidDataException>(() =>
            MarkdownDataUriDecoder.Decode(value, maximumDecodedBytes: 64));
    }

    [Fact]
    public void RejectsUnpairedLiteralSurrogatesAndOversizedHeaders()
    {
        string unpairedSurrogate = "data:text/plain," + new string('\uD800', 1);
        string oversizedHeader = "data:" + new string('x', 1025) + ",a";

        Assert.Throws<InvalidDataException>(() =>
            MarkdownDataUriDecoder.Decode(unpairedSurrogate, maximumDecodedBytes: 8));
        Assert.Throws<InvalidDataException>(() =>
            MarkdownDataUriDecoder.Decode(oversizedHeader, maximumDecodedBytes: 8));
    }

    [Fact]
    public void CancellationIsCheckedBeforeDecoding()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            MarkdownDataUriDecoder.Decode("data:;base64,AQID", 3, cancellation.Token));
    }

    [Fact]
    public void ArbitraryBinaryPayloadsRoundTripInEachEncodingAtTheExactBudget()
    {
        var random = new Random(2397);
        for (int length = 1; length <= 256; length++)
        {
            var expected = new byte[length];
            random.NextBytes(expected);
            string base64 = Convert.ToBase64String(expected);
            string percentBytes = string.Concat(Array.ConvertAll(expected, value => $"%{value:X2}"));
            string escapedBase64 = string.Concat(Array.ConvertAll(base64.ToCharArray(), value => $"%{(int)value:X2}"));

            Assert.Equal(expected, MarkdownDataUriDecoder.Decode("data:;base64," + base64, length));
            Assert.Equal(expected, MarkdownDataUriDecoder.Decode("data:," + percentBytes, length));
            Assert.Equal(expected, MarkdownDataUriDecoder.Decode("data:;base64," + escapedBase64, length));
        }
    }
}
