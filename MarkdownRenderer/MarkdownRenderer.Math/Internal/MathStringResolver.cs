using System.Globalization;
using System.Text;

namespace MarkdownRenderer.Math.Internal;

/// <summary>
/// Contains the trust boundary around host localization. A faulty provider can
/// never suppress a renderer diagnostic or exact-source fallback.
/// </summary>
internal static class MathStringResolver
{
    private const int MaximumLocalizedStringLength = 1024;

    internal static string Resolve(
        IMathStringProvider? provider,
        string key,
        string? languageTag)
    {
        string fallback = GetEnglishString(key);
        if (provider is null)
            return fallback;

        try
        {
            string? candidate = provider.GetString(key, languageTag);
            return string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaximumLocalizedStringLength
                ? fallback
                : candidate;
        }
        catch
        {
            return fallback;
        }
    }

    internal static string FormatOriginalTex(
        IMathStringProvider? provider,
        string? languageTag,
        string tex,
        CultureInfo fallbackCulture,
        int maximumLength = MathAccessibilityLimits.MaximumHelpTextLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tex);
        ArgumentNullException.ThrowIfNull(fallbackCulture);
        if (maximumLength < 2)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));

        cancellationToken.ThrowIfCancellationRequested();
        string format = Resolve(provider, MathStringKeys.OriginalTex, languageTag);
        cancellationToken.ThrowIfCancellationRequested();
        if (TryFormatSingleTexPlaceholder(
            format,
            tex,
            maximumLength,
            cancellationToken,
            out string result))
        {
            return result;
        }

        return TryFormatSingleTexPlaceholder(
            GetEnglishString(MathStringKeys.OriginalTex),
            tex,
            maximumLength,
            cancellationToken,
            out result)
            ? result
            : "Original TeX is available through copy.";
    }

    private static bool TryFormatSingleTexPlaceholder(
        string format,
        string tex,
        int maximumLength,
        CancellationToken cancellationToken,
        out string result)
    {
        int placeholderCount = 0;
        long outputLength = 0;
        for (int index = 0; index < format.Length; index++)
        {
            if ((index & 0x3f) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            char value = format[index];
            if (value == '{')
            {
                if (index + 1 < format.Length && format[index + 1] == '{')
                {
                    outputLength++;
                    index++;
                    continue;
                }

                if (index + 2 >= format.Length ||
                    format[index + 1] != '0' ||
                    format[index + 2] != '}')
                {
                    result = string.Empty;
                    return false;
                }

                placeholderCount++;
                if (placeholderCount != 1)
                {
                    result = string.Empty;
                    return false;
                }

                outputLength += tex.Length;
                index += 2;
            }
            else if (value == '}')
            {
                if (index + 1 >= format.Length || format[index + 1] != '}')
                {
                    result = string.Empty;
                    return false;
                }

                outputLength++;
                index++;
            }
            else
            {
                outputLength++;
            }

        }

        if (placeholderCount != 1)
        {
            result = string.Empty;
            return false;
        }

        bool truncated = outputLength > maximumLength;
        int contentLimit = truncated
            ? maximumLength - 1
            : checked((int)outputLength);
        var output = new StringBuilder(maximumLength);
        for (int index = 0; index < format.Length && output.Length < contentLimit; index++)
        {
            if ((index & 0x3f) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            char value = format[index];
            if (value == '{')
            {
                if (format[index + 1] == '{')
                {
                    output.Append('{');
                    index++;
                }
                else
                {
                    bool appendedAllTex = AppendBounded(
                        output,
                        tex,
                        contentLimit,
                        cancellationToken);
                    index += 2;
                    if (!appendedAllTex)
                        break;
                }
            }
            else if (value == '}')
            {
                output.Append('}');
                index++;
            }
            else
            {
                output.Append(value);
            }
        }

        if (truncated)
            output.Append('\u2026');

        cancellationToken.ThrowIfCancellationRequested();
        result = output.ToString();
        return true;
    }

    private static bool AppendBounded(
        StringBuilder output,
        string value,
        int contentLimit,
        CancellationToken cancellationToken)
    {
        int available = contentLimit - output.Length;
        if (available <= 0)
            return value.Length == 0;

        int take = System.Math.Min(available, value.Length);
        if (take < value.Length &&
            take > 0 &&
            char.IsHighSurrogate(value[take - 1]) &&
            char.IsLowSurrogate(value[take]))
        {
            take--;
        }

        cancellationToken.ThrowIfCancellationRequested();
        output.Append(value.AsSpan(0, take));
        cancellationToken.ThrowIfCancellationRequested();
        return take == value.Length;
    }

    internal static CultureInfo SnapshotCulture(CultureInfo? culture = null) =>
        CultureInfo.ReadOnly((CultureInfo)(culture ?? CultureInfo.CurrentUICulture).Clone());

    internal static string GetLanguageTag(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return string.IsNullOrWhiteSpace(culture.Name) ? "en-US" : culture.Name;
    }

    internal static string GetEnglishString(string key) => key switch
    {
        MathStringKeys.AutomationName => "Mathematical expression",
        MathStringKeys.OriginalTex => "Original TeX: {0}",
        MathStringKeys.Fraction => "fraction",
        MathStringKeys.Numerator => "numerator",
        MathStringKeys.Denominator => "denominator",
        MathStringKeys.EndFraction => "end fraction",
        MathStringKeys.SquareRoot => "square root of",
        MathStringKeys.EndRoot => "end root",
        MathStringKeys.Superscript => "superscript",
        MathStringKeys.EndSuperscript => "end superscript",
        MathStringKeys.Subscript => "subscript",
        MathStringKeys.EndSubscript => "end subscript",
        MathStringKeys.Plus => "plus",
        MathStringKeys.Minus => "minus",
        MathStringKeys.EqualsOperator => "equals",
        MathStringKeys.Times => "times",
        MathStringKeys.DividedBy => "divided by",
        MathStringKeys.InvalidFormula => "The formula is not valid TeX.",
        MathStringKeys.UnmatchedInlineDelimiter => "Unmatched inline-math delimiter.",
        MathStringKeys.UnmatchedDisplayDelimiter => "Unmatched display-math delimiter.",
        MathStringKeys.SourceTooLong => "The formula exceeds the configured source-length limit.",
        MathStringKeys.NestingTooDeep => "The formula exceeds the configured nesting-depth limit.",
        MathStringKeys.SceneTooComplex => "The formula exceeds the configured scene-complexity limit.",
        MathStringKeys.SceneTooLarge => "The formula exceeds the configured scene-size limit.",
        MathStringKeys.WorkingMemoryExceeded => "The formula exceeds the configured working-memory limit.",
        MathStringKeys.ProcessingTimedOut => "The formula exceeded the configured processing deadline.",
        MathStringKeys.ProcessingFailed => "The formula could not be rendered.",
        _ => "The formula could not be rendered.",
    };
}
