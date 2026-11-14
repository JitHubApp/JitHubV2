using System.Globalization;
using MarkdownRenderer.Math.Accessibility.Internal;
using MarkdownRenderer.Math.Internal.Processing;
using MarkdownRenderer.Math.Localization;
using MarkdownRenderer.Math.Localization.Internal;

namespace MarkdownRenderer.Math.Accessibility;

/// <summary>
/// Cancellation-aware structural formatter for common TeX constructs. Hosts can
/// replace any phrase through <see cref="IMathStringProvider"/> or replace the
/// formatter completely through <see cref="IMathAccessibilityFormatter"/>.
/// </summary>
/// <remarks>
/// Direct calls begin synchronously on the calling thread. A supplied string
/// provider must therefore be prompt and non-blocking; use
/// <see cref="MathFormulaProcessor.ProcessAsync"/> when caller-thread isolation
/// and a processing deadline are required. Structural speech and help text are
/// hard-bounded for safe UI Automation publication.
/// </remarks>
public sealed class DefaultMathAccessibilityFormatter : IMathAccessibilityFormatter
{
    private readonly IMathStringProvider? _strings;
    private readonly CultureInfo _localizationCulture;

    /// <summary>Creates the formatter.</summary>
    public DefaultMathAccessibilityFormatter(IMathStringProvider? strings = null)
        : this(strings, MathStringResolver.SnapshotCulture())
    {
    }

    internal DefaultMathAccessibilityFormatter(
        IMathStringProvider? strings,
        CultureInfo localizationCulture)
    {
        _strings = strings;
        _localizationCulture = MathStringResolver.SnapshotCulture(localizationCulture);
    }

    /// <inheritdoc />
    public ValueTask<MathAccessibilityDescription> FormatAsync(
        MathFormulaRequest request,
        CancellationToken cancellationToken = default)
        => FormatAsync(request, request?.LanguageTag, cancellationToken);

    internal ValueTask<MathAccessibilityDescription> FormatAsync(
        MathFormulaRequest request,
        string? effectiveLanguageTag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var resolvedStrings = new Dictionary<string, string>(StringComparer.Ordinal);
        string speech = MathStructuralSpeechFormatter.Format(
            request.TexSource,
            ResolveOnce,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string helpText = MathStringResolver.FormatOriginalTex(
            _strings,
            effectiveLanguageTag,
            request.TexSource,
            _localizationCulture,
            MathAccessibilityLimits.MaximumHelpTextLength,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(new MathAccessibilityDescription(
            ResolveOnce(MathStringKeys.AutomationName),
            speech,
            helpText,
            request.TexSource));

        string ResolveOnce(string key)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resolvedStrings.TryGetValue(key, out string? value))
            {
                value = Resolve(key, effectiveLanguageTag, cancellationToken);
                resolvedStrings.Add(key, value);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return value;
        }
    }

    private string Resolve(
        string key,
        string? languageTag,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string value = MathStringResolver.Resolve(_strings, key, languageTag);
        cancellationToken.ThrowIfCancellationRequested();
        return value;
    }
}
