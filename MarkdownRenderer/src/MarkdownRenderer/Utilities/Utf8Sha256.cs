using System;
using System.Security.Cryptography;
using System.Text;

namespace MarkdownRenderer.Utilities;

internal static class Utf8Sha256
{
    private const int WholeBufferMaxChars = 4096;
    private const int StreamingBufferBytes = 4096;

    internal static string GetHexString(string? text, int digestBytes = 32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(digestBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digestBytes, 32);

        // Preserve the one-shot path for ordinary keys, with at most 12 KiB
        // of encoded input. Larger, potentially source-sized keys must not
        // allocate an input-sized temporary or enter the large object heap.
        if (text.AsSpan().Length <= WholeBufferMaxChars)
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
            return Convert.ToHexString(digest.AsSpan(0, digestBytes));
        }

        ReadOnlySpan<char> remaining = text.AsSpan();
        Span<byte> digestBytesOnStack = stackalloc byte[32];
        Span<byte> utf8 = stackalloc byte[StreamingBufferBytes];
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            // Keep Encoding.UTF8's existing replacement fallback, including
            // malformed surrogate pairs and pairs crossing a buffer boundary.
            Encoder encoder = Encoding.UTF8.GetEncoder();
            bool completed;
            do
            {
                encoder.Convert(remaining, utf8, flush: true,
                    out int charsUsed, out int bytesUsed, out completed);
                hash.AppendData(utf8[..bytesUsed]);
                remaining = remaining[charsUsed..];
            }
            while (!completed);
            hash.GetHashAndReset(digestBytesOnStack);
            return Convert.ToHexString(digestBytesOnStack[..digestBytes]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
            CryptographicOperations.ZeroMemory(digestBytesOnStack);
        }
    }
}
