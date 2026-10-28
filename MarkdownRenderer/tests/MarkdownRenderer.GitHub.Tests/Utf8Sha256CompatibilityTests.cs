using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Utilities;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class Utf8Sha256CompatibilityTests
{
    [Fact]
    public void Utf8Sha256MatchesOriginalNullEmptyAndBasicInputs()
    {
        foreach (string? input in new string?[]
        {
            null,
            string.Empty,
            "key",
            "https://example.test/a?token=secret",
        })
            Assert.Equal(Original(input), Utf8Sha256.GetHexString(input));
    }

    [Fact]
    public void Utf8Sha256MatchesMalformedUnicodeAndSeededRandomUtf16()
    {
        string[] fixedCases =
        [
            "cafe\u0301 \U0001F30D",
            "漢字\U0001F680",
            "\uD800",
            "\uDC00",
            "a\uD800b\uDC00c",
            "\uD83D\uDE00",
        ];
        foreach (string value in fixedCases)
            Assert.Equal(Original(value), Utf8Sha256.GetHexString(value));

        var random = new Random(1729);
        for (int sample = 0; sample < 256; sample++)
        {
            var chars = new char[random.Next(1, 16384)];
            for (int index = 0; index < chars.Length; index++)
                chars[index] = (char)random.Next(0, 65536);
            string value = new(chars);
            Assert.Equal(Original(value), Utf8Sha256.GetHexString(value));
        }
    }

    [Fact]
    public void Utf8Sha256MatchesStreamingAndReplacementFallbackBoundaries()
    {
        foreach (int length in new[] { 4093, 4094, 4095, 4096, 4097, 8191, 8192 })
        {
            string prefix = new('x', length);
            foreach (string value in new[]
            {
                prefix,
                prefix + "\U0001F30D!",
                prefix + "\uD800z",
                new string('\u6F22', length) + "\U0001F680",
            })
                Assert.Equal(Original(value), Utf8Sha256.GetHexString(value));
        }
    }

    [Fact]
    public void Utf8Sha256ReturnsExactDigestPrefixes()
    {
        const string input = "prefix \U0001F30D cafe\u0301";
        string full = Original(input);
        foreach (int bytes in new[] { 1, 8, 16, 32 })
            Assert.Equal(full[..(bytes * 2)], Utf8Sha256.GetHexString(input, bytes));
    }

    [Fact]
    public void Utf8Sha256RejectsInvalidDigestLengths()
    {
        foreach (int bytes in new[] { -1, 0, 33, int.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => Utf8Sha256.GetHexString("key", bytes));
    }

    [Fact]
    public void Utf8Sha256EightMiBInputStaysWithinBoundedManagedAllocation()
    {
        string input = new('x', 8 * 1024 * 1024);
        string expected = Original(input);
        for (int warmup = 0; warmup < 8; warmup++)
            _ = Utf8Sha256.GetHexString(input);

        long before = GC.GetAllocatedBytesForCurrentThread();
        string actual = Utf8Sha256.GetHexString(input);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expected, actual);
        Assert.InRange(allocated, 0, 8192);
    }

    [Fact]
    public void ImageBoxSecurityPartitionHashMatchesExactUtf8Sha256()
    {
        MethodInfo method = typeof(ImageBox).GetMethod(
            "HashSvgSecurityPartition",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (string? partition in new string?[]
        {
            null,
            string.Empty,
            "https://example.test/trust-domain-a",
            "https://例え.test/partition/\U0001F30D",
        })
        {
            string expected = Original(partition);
            string actual = Assert.IsType<string>(method.Invoke(null, [partition]));
            Assert.Equal(expected, actual);
            Assert.Equal(64, actual.Length);
            if (!string.IsNullOrEmpty(partition))
                Assert.DoesNotContain(partition, actual, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SafeHtmlMediaPlaceholderPreservesOriginalFirstEightDigestBytes()
    {
        const string source = "https://private.example.test/video.mp4?token=secret-\U0001F30D";
        MethodInfo method = typeof(SafeHtmlMediaRunFactory).GetMethod(
            "CreateMediaPlaceholder",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        string placeholder = Assert.IsType<string>(method.Invoke(null, [true, source]));
        const string prefix = "data:image/svg+xml;base64,";
        Assert.StartsWith(prefix, placeholder, StringComparison.Ordinal);
        string svg = Encoding.UTF8.GetString(Convert.FromBase64String(placeholder[prefix.Length..]));
        byte[] expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        string expectedMetadata = "<metadata>media-" + Convert.ToHexString(expectedDigest.AsSpan(0, 8)) + "</metadata>";
        Assert.Contains(expectedMetadata, svg, StringComparison.Ordinal);
        Assert.DoesNotContain(source, svg, StringComparison.Ordinal);
    }

    private static string Original(string? text) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)));
}
