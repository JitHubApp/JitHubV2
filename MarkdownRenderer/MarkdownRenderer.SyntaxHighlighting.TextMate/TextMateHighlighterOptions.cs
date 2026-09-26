using System;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate;

/// <summary>
/// Resource limits for TextMate code-block highlighting.
/// </summary>
/// <remarks>
/// Limits are evaluated before a grammar provider is invoked. Inputs that exceed
/// a limit are rendered as unhighlighted code rather than being partially
/// tokenized. Instances are immutable and thread-safe.
/// </remarks>
public sealed class TextMateHighlighterOptions
{
    /// <summary>The default maximum code-block length, in UTF-16 code units.</summary>
    public const int DefaultMaximumCodeLength = 200_000;

    /// <summary>The default maximum number of lines in one code block.</summary>
    public const int DefaultMaximumLineCount = 5_000;

    /// <summary>The default maximum line length, in UTF-16 code units.</summary>
    public const int DefaultMaximumLineLength = 65_536;

    /// <summary>The default maximum number of foreground spans in one result.</summary>
    public const int DefaultMaximumSpanCount = 100_000;

    /// <summary>The default retained result-cache budget.</summary>
    public const int DefaultCacheBudgetBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The default aggregate processing budget for one logical line.
    /// </summary>
    public static readonly TimeSpan DefaultMaximumLineProcessingTime =
        TimeSpan.FromMilliseconds(250);

    /// <summary>Creates validated TextMate resource limits.</summary>
    public TextMateHighlighterOptions(
        int maximumCodeLength = DefaultMaximumCodeLength,
        int maximumLineCount = DefaultMaximumLineCount,
        int maximumLineLength = DefaultMaximumLineLength,
        int maximumSpanCount = DefaultMaximumSpanCount,
        int cacheBudgetBytes = DefaultCacheBudgetBytes,
        TimeSpan? maximumLineProcessingTime = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCodeLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSpanCount);
        ArgumentOutOfRangeException.ThrowIfNegative(cacheBudgetBytes);

        TimeSpan effectiveMaximumLineProcessingTime =
            maximumLineProcessingTime ?? DefaultMaximumLineProcessingTime;
        if (effectiveMaximumLineProcessingTime <= TimeSpan.Zero ||
            effectiveMaximumLineProcessingTime > TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLineProcessingTime),
                "The per-line processing limit must be greater than zero and no more than one second.");
        }

        MaximumCodeLength = maximumCodeLength;
        MaximumLineCount = maximumLineCount;
        MaximumLineLength = maximumLineLength;
        MaximumSpanCount = maximumSpanCount;
        CacheBudgetBytes = cacheBudgetBytes;
        MaximumLineProcessingTime = effectiveMaximumLineProcessingTime;
    }

    /// <summary>Maximum code-block length, in UTF-16 code units.</summary>
    public int MaximumCodeLength { get; }

    /// <summary>Maximum number of logical lines in one code block.</summary>
    public int MaximumLineCount { get; }

    /// <summary>Maximum logical-line length, in UTF-16 code units.</summary>
    public int MaximumLineLength { get; }

    /// <summary>Maximum number of foreground spans accepted from a provider.</summary>
    public int MaximumSpanCount { get; }

    /// <summary>
    /// Maximum estimated bytes retained by the weighted LRU result cache. A value
    /// of zero disables result caching without disabling in-flight deduplication.
    /// </summary>
    public int CacheBudgetBytes { get; }

    /// <summary>
    /// Aggregate processing budget for one logical line. The bundled providers
    /// split this budget into synchronous slices of at most 16 milliseconds and
    /// check cancellation between slices.
    /// </summary>
    public TimeSpan MaximumLineProcessingTime { get; }
}
