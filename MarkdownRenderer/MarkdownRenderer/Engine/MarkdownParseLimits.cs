using System;

namespace MarkdownRenderer;

/// <summary>
/// Immutable admission limits for uncached markdown parsing. Source byte values
/// are estimated from UTF-16 storage and include both running and queued work.
/// </summary>
public sealed class MarkdownParseLimits : IEquatable<MarkdownParseLimits>
{
    /// <summary>Default maximum source length: 4 Mi UTF-16 code units.</summary>
    public const int DefaultMaximumSourceLength = 4 * 1024 * 1024;

    /// <summary>Default maximum concurrently executing unique parses.</summary>
    public const int DefaultMaximumConcurrentParseCount = 4;

    /// <summary>Default maximum running plus queued unique parses.</summary>
    public const int DefaultMaximumOutstandingParseCount = 16;

    /// <summary>Default maximum retained UTF-16 source bytes for outstanding parses.</summary>
    public const long DefaultMaximumOutstandingSourceBytes = 64L * 1024 * 1024;

    /// <summary>Creates validated parse admission limits.</summary>
    public MarkdownParseLimits(
        int maximumSourceLength = DefaultMaximumSourceLength,
        int maximumConcurrentParseCount = DefaultMaximumConcurrentParseCount,
        int maximumOutstandingParseCount = DefaultMaximumOutstandingParseCount,
        long maximumOutstandingSourceBytes = DefaultMaximumOutstandingSourceBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSourceLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrentParseCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutstandingParseCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutstandingSourceBytes);
        if (maximumConcurrentParseCount > maximumOutstandingParseCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrentParseCount),
                "Concurrent parse count cannot exceed the outstanding parse count.");
        }

        long maximumSourceBytes = checked((long)maximumSourceLength * sizeof(char));
        if (maximumOutstandingSourceBytes < maximumSourceBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumOutstandingSourceBytes),
                "Outstanding source bytes must admit at least one maximum-length source.");
        }

        MaximumSourceLength = maximumSourceLength;
        MaximumConcurrentParseCount = maximumConcurrentParseCount;
        MaximumOutstandingParseCount = maximumOutstandingParseCount;
        MaximumOutstandingSourceBytes = maximumOutstandingSourceBytes;
    }

    /// <summary>Gets the audited default admission limits.</summary>
    public static MarkdownParseLimits Default { get; } = new();

    /// <summary>Gets the maximum source length in UTF-16 code units.</summary>
    public int MaximumSourceLength { get; }

    /// <summary>Gets the maximum concurrently executing unique parse count.</summary>
    public int MaximumConcurrentParseCount { get; }

    /// <summary>Gets the maximum running plus queued unique parse count.</summary>
    public int MaximumOutstandingParseCount { get; }

    /// <summary>Gets the maximum retained UTF-16 bytes across outstanding sources.</summary>
    public long MaximumOutstandingSourceBytes { get; }

    /// <inheritdoc />
    public bool Equals(MarkdownParseLimits? other) =>
        other is not null &&
        MaximumSourceLength == other.MaximumSourceLength &&
        MaximumConcurrentParseCount == other.MaximumConcurrentParseCount &&
        MaximumOutstandingParseCount == other.MaximumOutstandingParseCount &&
        MaximumOutstandingSourceBytes == other.MaximumOutstandingSourceBytes;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as MarkdownParseLimits);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        MaximumSourceLength,
        MaximumConcurrentParseCount,
        MaximumOutstandingParseCount,
        MaximumOutstandingSourceBytes);
}
