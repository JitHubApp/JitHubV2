using System.IO;
using System.Text;
using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownDataUriDecoderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(127)]
    [InlineData(4097)]
    public void BinaryPayloadRoundTripsAcrossWholeBufferAndStreamingParser(int length)
    {
        byte[] expected = Enumerable.Range(0, length)
            .Select(index => (byte)((index * 37 + 11) & 255)).ToArray();
        string encoded = Convert.ToBase64String(expected);
        var mixed = new StringBuilder(encoded.Length * 3);
        for (int index = 0; index < encoded.Length; index++)
        {
            if ((index & 1) == 0)
                mixed.Append('%').Append(((int)encoded[index]).ToString("X2"));
            else
                mixed.Append(encoded[index]);
            if ((index & 15) == 0)
                mixed.Append(" \r\n");
        }

        foreach (string payload in new[] { encoded, Uri.EscapeDataString(encoded), mixed.ToString() })
        {
            string source = "data:application/octet-stream;base64," + payload;
            Assert.Equal(length, MarkdownDataUriDecoder.GetDecodedLength(source, length));
            Assert.Equal(expected, MarkdownDataUriDecoder.Decode(source, length));
        }
    }

    [Theory]
    [InlineData("data:text/plain;base64,SGVsbG8=", "Hello")]
    [InlineData("data:text/plain;base64,SGVs \r\n bG8=", "Hello")]
    [InlineData("data:text/plain;BASE64,%53%47%56%73%62%47%38%3D", "Hello")]
    [InlineData("data:text/plain,Hello%20world", "Hello world")]
    [InlineData("data:text/plain,cafe\u0301%20\U0001F30D", "cafe\u0301 \U0001F30D")]
    [InlineData("data:text/plain,cafe%CC%81%20%F0%9F%8C%8D", "cafe\u0301 \U0001F30D")]
    public void GetDecodedLengthMatchesDecodedBytes(string dataUri, string expectedText)
    {
        int length = MarkdownDataUriDecoder.GetDecodedLength(dataUri, maximumDecodedBytes: 128);
        byte[] decoded = MarkdownDataUriDecoder.Decode(dataUri, maximumDecodedBytes: 128);
        byte[] expected = Encoding.UTF8.GetBytes(expectedText);

        Assert.Equal(expected.Length, length);
        Assert.Equal(decoded.Length, length);
        Assert.Equal(expected, decoded);
    }

    [Theory]
    [InlineData("data:text/plain,")]
    [InlineData("data:text/plain;base64,")]
    [InlineData("data:text/plain,%")]
    [InlineData("data:text/plain,%XZ")]
    [InlineData("data:text/plain;base64,SGVsbG8%2")]
    [InlineData("data:text/plain;base64,SGVsbG8%GG")]
    [InlineData("data:text/plain;base64,AA=A")]
    [InlineData("data:text/plain;base64,AA==AAAA")]
    [InlineData("data:text/plain;base64,AAAA=")]
    [InlineData("data:text/plain;base64,====")]
    [InlineData("data:text/plain;base64,A%3D%3DA")]
    [InlineData("not-a-data-uri")]
    public void EmptyOrMalformedPayloadFailsBeforeLengthOrDecode(string dataUri)
    {
        Assert.Throws<InvalidDataException>(() =>
            MarkdownDataUriDecoder.GetDecodedLength(dataUri, maximumDecodedBytes: 128));
        Assert.Throws<InvalidDataException>(() =>
            MarkdownDataUriDecoder.Decode(dataUri, maximumDecodedBytes: 128));
    }

    [Fact]
    public void UnpairedUnicodeSurrogateFailsBeforeLengthOrDecode()
    {
        string dataUri = string.Concat("data:text/plain,", (char)0xD800);

        Assert.Throws<InvalidDataException>(() =>
            MarkdownDataUriDecoder.GetDecodedLength(dataUri, maximumDecodedBytes: 128));
        Assert.Throws<InvalidDataException>(() =>
            MarkdownDataUriDecoder.Decode(dataUri, maximumDecodedBytes: 128));
    }

    [Theory]
    [InlineData("data:text/plain,1234")]
    [InlineData("data:text/plain;base64,MTIzNA==")]
    public void ExactLimitIsAcceptedAndOneByteBelowFails(string dataUri)
    {
        Assert.Equal(4, MarkdownDataUriDecoder.GetDecodedLength(dataUri, maximumDecodedBytes: 4));
        Assert.Equal(4, MarkdownDataUriDecoder.Decode(dataUri, maximumDecodedBytes: 4).Length);

        Assert.Throws<InvalidDataException>(() =>
            MarkdownDataUriDecoder.GetDecodedLength(dataUri, maximumDecodedBytes: 3));
        Assert.Throws<InvalidDataException>(() =>
            MarkdownDataUriDecoder.Decode(dataUri, maximumDecodedBytes: 3));
    }

    [Theory]
    [InlineData("data:text/plain,12345678901234567")]
    [InlineData("data:text/plain;base64,MTIzNDU2Nzg5MDEyMzQ1Njc=")]
    public void EncodedPayloadLimitIsSharedByMeasurementAndDecode(string dataUri)
    {
        Assert.Throws<InvalidDataException>(() =>
            MarkdownDataUriDecoder.GetDecodedLength(dataUri, maximumDecodedBytes: 0));
        Assert.Throws<InvalidDataException>(() =>
            MarkdownDataUriDecoder.Decode(dataUri, maximumDecodedBytes: 0));
    }

    [Fact]
    public void CanceledMeasurementAndDecodeFailBeforeAllocatingPayloadBytes()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        const string dataUri = "data:text/plain;base64,SGVsbG8=";

        Assert.Throws<OperationCanceledException>(() =>
            MarkdownDataUriDecoder.GetDecodedLength(
                dataUri,
                maximumDecodedBytes: 128,
                cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            MarkdownDataUriDecoder.Decode(
                dataUri,
                maximumDecodedBytes: 128,
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public void NegativeLimitIsRejectedByBothOperations()
    {
        const string dataUri = "data:text/plain,hello";

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MarkdownDataUriDecoder.GetDecodedLength(dataUri, maximumDecodedBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MarkdownDataUriDecoder.Decode(dataUri, maximumDecodedBytes: -1));
    }
}
