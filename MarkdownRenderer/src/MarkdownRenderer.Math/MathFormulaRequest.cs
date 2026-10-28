namespace MarkdownRenderer.Math;

/// <summary>Identifies inline or display formula layout.</summary>
public enum MathFormulaDisplayMode
{
    /// <summary>Flows with surrounding text.</summary>
    Inline,
    /// <summary>Occupies a display-math block.</summary>
    Display,
}

/// <summary>
/// Immutable input for one atomic formula. Ranges are absolute, half-open UTF-16 offsets.
/// </summary>
public sealed class MathFormulaRequest
{
    /// <summary>Creates an immutable atomic formula request.</summary>
    public MathFormulaRequest(
        string originalSource,
        string texSource,
        MathSourceRange sourceRange,
        MathSourceRange contentRange,
        MathFormulaDisplayMode displayMode,
        string? languageTag = null)
    {
        ArgumentNullException.ThrowIfNull(originalSource);
        ArgumentNullException.ThrowIfNull(texSource);

        if (originalSource.Length != sourceRange.Length)
        {
            throw new ArgumentException(
                "The exact original formula source must have the same UTF-16 length as its source range.",
                nameof(originalSource));
        }

        if (texSource.Length != contentRange.Length)
        {
            throw new ArgumentException(
                "The exact TeX source must have the same UTF-16 length as its content range.",
                nameof(texSource));
        }

        if (!sourceRange.Contains(contentRange))
        {
            throw new ArgumentOutOfRangeException(nameof(contentRange), "The content range must be inside the formula range.");
        }

        OriginalSource = originalSource;
        TexSource = texSource;
        SourceRange = sourceRange;
        ContentRange = contentRange;
        DisplayMode = displayMode;
        LanguageTag = string.IsNullOrWhiteSpace(languageTag) ? null : languageTag;
    }

    /// <summary>The exact source, including delimiters, used for lossless fallback.</summary>
    public string OriginalSource { get; }

    /// <summary>The exact source between the delimiters. It is not normalized.</summary>
    public string TexSource { get; }

    /// <summary>Gets the exact delimited formula range in the Markdown source.</summary>
    public MathSourceRange SourceRange { get; }

    /// <summary>Gets the TeX content range inside <see cref="SourceRange"/>.</summary>
    public MathSourceRange ContentRange { get; }

    /// <summary>Gets whether this formula is inline or display math.</summary>
    public MathFormulaDisplayMode DisplayMode { get; }

    /// <summary>Gets the optional BCP-47 language tag used for accessible text.</summary>
    public string? LanguageTag { get; }
}
