namespace MarkdownRenderer.Html;

/// <summary>
/// Immutable resource ceilings for native safe-HTML parsing. Values are measured in
/// UTF-16 code units or element counts as named.
/// </summary>
public sealed class SafeHtmlBudgets : IEquatable<SafeHtmlBudgets>
{
    /// <summary>Default maximum HTML input length in UTF-16 code units.</summary>
    public const int DefaultMaxInputLength = 4 * 1024 * 1024;
    /// <summary>Default maximum parsed node count.</summary>
    public const int DefaultMaxNodeCount = 20_000;
    /// <summary>Default maximum element nesting depth.</summary>
    public const int DefaultMaxNestingDepth = 64;
    /// <summary>Default maximum attributes retained per element.</summary>
    public const int DefaultMaxAttributeCount = 32;
    /// <summary>Default maximum attribute value length in UTF-16 code units.</summary>
    public const int DefaultMaxAttributeValueLength = 16 * 1024;
    /// <summary>Default maximum tag length in UTF-16 code units.</summary>
    public const int DefaultMaxTagLength = 64 * 1024;

    /// <summary>Creates validated positive parser ceilings.</summary>
    /// <param name="maxInputLength">Maximum input length in UTF-16 code units.</param>
    /// <param name="maxNodeCount">Maximum parsed node count.</param>
    /// <param name="maxNestingDepth">Maximum element nesting depth.</param>
    /// <param name="maxAttributeCount">Maximum attributes retained per element.</param>
    /// <param name="maxAttributeValueLength">Maximum attribute value length.</param>
    /// <param name="maxTagLength">Maximum tag length.</param>
    public SafeHtmlBudgets(
        int maxInputLength = DefaultMaxInputLength,
        int maxNodeCount = DefaultMaxNodeCount,
        int maxNestingDepth = DefaultMaxNestingDepth,
        int maxAttributeCount = DefaultMaxAttributeCount,
        int maxAttributeValueLength = DefaultMaxAttributeValueLength,
        int maxTagLength = DefaultMaxTagLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInputLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxNodeCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxNestingDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttributeCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttributeValueLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTagLength);

        MaxInputLength = maxInputLength;
        MaxNodeCount = maxNodeCount;
        MaxNestingDepth = maxNestingDepth;
        MaxAttributeCount = maxAttributeCount;
        MaxAttributeValueLength = maxAttributeValueLength;
        MaxTagLength = maxTagLength;
    }

    /// <summary>Gets the audited default ceilings.</summary>
    public static SafeHtmlBudgets Default { get; } = new();

    /// <summary>Gets the maximum HTML input length.</summary>
    public int MaxInputLength { get; }

    /// <summary>Gets the maximum parsed node count.</summary>
    public int MaxNodeCount { get; }

    /// <summary>Gets the maximum element nesting depth.</summary>
    public int MaxNestingDepth { get; }

    /// <summary>Gets the maximum attributes retained per element.</summary>
    public int MaxAttributeCount { get; }

    /// <summary>Gets the maximum attribute value length.</summary>
    public int MaxAttributeValueLength { get; }

    /// <summary>Gets the maximum tag length.</summary>
    public int MaxTagLength { get; }

    /// <summary>Returns component-wise lower ceilings, suitable for a stricter host policy.</summary>
    public SafeHtmlBudgets LoweredTo(SafeHtmlBudgets ceilings)
    {
        ArgumentNullException.ThrowIfNull(ceilings);
        return new SafeHtmlBudgets(
            global::System.Math.Min(MaxInputLength, ceilings.MaxInputLength),
            global::System.Math.Min(MaxNodeCount, ceilings.MaxNodeCount),
            global::System.Math.Min(MaxNestingDepth, ceilings.MaxNestingDepth),
            global::System.Math.Min(MaxAttributeCount, ceilings.MaxAttributeCount),
            global::System.Math.Min(MaxAttributeValueLength, ceilings.MaxAttributeValueLength),
            global::System.Math.Min(MaxTagLength, ceilings.MaxTagLength));
    }

    /// <inheritdoc />
    public bool Equals(SafeHtmlBudgets? other) =>
        other is not null &&
        MaxInputLength == other.MaxInputLength &&
        MaxNodeCount == other.MaxNodeCount &&
        MaxNestingDepth == other.MaxNestingDepth &&
        MaxAttributeCount == other.MaxAttributeCount &&
        MaxAttributeValueLength == other.MaxAttributeValueLength &&
        MaxTagLength == other.MaxTagLength;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SafeHtmlBudgets);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        MaxInputLength,
        MaxNodeCount,
        MaxNestingDepth,
        MaxAttributeCount,
        MaxAttributeValueLength,
        MaxTagLength);
}
