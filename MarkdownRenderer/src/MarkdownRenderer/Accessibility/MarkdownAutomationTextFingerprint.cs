using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Fixed-size identity for an immutable source or semantic-text projection.
/// Created with the background snapshot; UI publication compares only scalars.
/// </summary>
internal readonly struct MarkdownAutomationTextFingerprint : IEquatable<MarkdownAutomationTextFingerprint>
{
    private readonly ulong _word0;
    private readonly ulong _word1;
    private readonly ulong _word2;
    private readonly ulong _word3;

    private MarkdownAutomationTextFingerprint(int length, ulong word0, ulong word1, ulong word2, ulong word3)
    {
        Length = length;
        _word0 = word0;
        _word1 = word1;
        _word2 = word2;
        _word3 = word3;
    }

    internal readonly int Length;

    internal static MarkdownAutomationTextFingerprint Compute(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(MemoryMarshal.AsBytes(text.AsSpan()), digest);
        return FromSha256(text.Length, digest);
    }

    internal static MarkdownAutomationTextFingerprint FromSha256(int length, ReadOnlySpan<byte> digest)
    {
        if (digest.Length != 32)
            throw new ArgumentException("A SHA-256 digest must contain 32 bytes.", nameof(digest));

        return new MarkdownAutomationTextFingerprint(
            length,
            BinaryPrimitives.ReadUInt64LittleEndian(digest),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[24..]));
    }

    public bool Equals(MarkdownAutomationTextFingerprint other) =>
        Length == other.Length &&
        _word0 == other._word0 &&
        _word1 == other._word1 &&
        _word2 == other._word2 &&
        _word3 == other._word3;

    public override bool Equals(object? obj) =>
        obj is MarkdownAutomationTextFingerprint other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Length, _word0, _word1, _word2, _word3);
}
