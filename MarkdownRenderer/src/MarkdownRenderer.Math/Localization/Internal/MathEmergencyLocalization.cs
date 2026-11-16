using System.Globalization;
using MarkdownRenderer.Math.Accessibility;
using MarkdownRenderer.Math.Internal.Processing;

namespace MarkdownRenderer.Math.Localization.Internal;

/// <summary>
/// Builds the bounded timeout fallback a formula reports when processing
/// misses its deadline: the always-available English snapshot, and the one
/// resolved through the host string provider.
/// </summary>
internal static class MathEmergencyLocalization
{
    private const int MaximumEmergencyTexPreviewLength = 256;
    private const int MaximumEmergencyHelpTextLength = 2048;

    internal static MathEmergencyLocalizationSnapshot CreateLocalizedEmergencySnapshot(
        IMathStringProvider? strings,
        MathFormulaRequest request,
        string languageTag,
        CultureInfo localizationCulture,
        string texPreview,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string automationName = MathStringResolver.Resolve(
            strings,
            MathStringKeys.AutomationName,
            languageTag);

        // Every synchronous callback has its own preceding checkpoint. In
        // particular, a provider blocked in one call cannot start the next call
        // after the deadline has been observed.
        cancellationToken.ThrowIfCancellationRequested();
        string helpText = MathStringResolver.FormatOriginalTex(
            strings,
            languageTag,
            texPreview,
            localizationCulture,
            MaximumEmergencyHelpTextLength,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        string timeoutMessage = MathStringResolver.Resolve(
            strings,
            MathStringKeys.ProcessingTimedOut,
            languageTag);
        cancellationToken.ThrowIfCancellationRequested();

        return new MathEmergencyLocalizationSnapshot(
            new MathAccessibilityDescription(
                automationName,
                texPreview,
                helpText,
                request.TexSource),
            new MathFormulaDiagnostic(
                "MATH106",
                timeoutMessage,
                request.ContentRange),
            texPreview);
    }

    internal static MathEmergencyLocalizationSnapshot CreateEnglishEmergencySnapshot(
        MathFormulaRequest request)
    {
        string texPreview = CreateBoundedText(
            request.TexSource,
            MaximumEmergencyTexPreviewLength);
        string helpText = CreateBoundedText(
            "Original TeX: " + texPreview,
            MaximumEmergencyHelpTextLength);
        return new MathEmergencyLocalizationSnapshot(
            new MathAccessibilityDescription(
                MathStringResolver.GetEnglishString(MathStringKeys.AutomationName),
                texPreview,
                helpText,
                request.TexSource),
            new MathFormulaDiagnostic(
                "MATH106",
                MathStringResolver.GetEnglishString(MathStringKeys.ProcessingTimedOut),
                request.ContentRange),
            texPreview);
    }

    private static string CreateBoundedText(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
            return value;

        int prefixLength = maximumLength - 1;
        if (prefixLength > 0 &&
            char.IsHighSurrogate(value[prefixLength - 1]) &&
            char.IsLowSurrogate(value[prefixLength]))
        {
            prefixLength--;
        }

        return string.Create(
            prefixLength + 1,
            (Value: value, PrefixLength: prefixLength),
            static (destination, state) =>
            {
                state.Value.AsSpan(0, state.PrefixLength).CopyTo(destination);
                destination[^1] = '\u2026';
            });
    }
}
