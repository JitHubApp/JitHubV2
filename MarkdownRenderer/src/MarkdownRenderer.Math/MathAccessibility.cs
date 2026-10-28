using System.Globalization;
using MarkdownRenderer.Math.Internal;

namespace MarkdownRenderer.Math;

/// <summary>
/// Localized, renderer-independent accessibility text for one formula.
/// </summary>
public sealed class MathAccessibilityDescription
{
    /// <summary>Creates the accessible representation of one formula.</summary>
    public MathAccessibilityDescription(
        string automationName,
        string structuralSpeech,
        string helpText,
        string copyText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(automationName);
        ArgumentNullException.ThrowIfNull(structuralSpeech);
        ArgumentNullException.ThrowIfNull(helpText);
        ArgumentNullException.ThrowIfNull(copyText);

        AutomationName = automationName;
        StructuralSpeech = structuralSpeech;
        HelpText = helpText;
        CopyText = copyText;
    }

    /// <summary>Gets the short name exposed for the atomic formula.</summary>
    public string AutomationName { get; }

    /// <summary>Gets the structural spoken representation of the formula.</summary>
    public string StructuralSpeech { get; }

    /// <summary>
    /// Gets help text containing the original TeX, or a bounded preview when
    /// processing is rejected or times out before full accessibility formatting.
    /// </summary>
    public string HelpText { get; }

    /// <summary>Text copied for the formula; normally the original TeX without delimiters.</summary>
    public string CopyText { get; }
}

/// <summary>
/// Formats structural speech without exposing a parser or drawing-engine type.
/// </summary>
/// <remarks>
/// A formatter can be invoked concurrently and must remain thread-safe and
/// behaviorally stable for the processor or engine lifetime. The processor
/// passes the caller's original request instance unchanged; replacement
/// formatters own their localization policy when its language tag is null.
/// Processor invocations use bounded process-wide callback admission. An
/// uncooperative callback may finish after the caller receives a timeout result,
/// and continues holding its admission slot until its actual completion.
/// </remarks>
public interface IMathAccessibilityFormatter
{
    /// <summary>Creates accessible text for one atomic formula.</summary>
    ValueTask<MathAccessibilityDescription> FormatAsync(
        MathFormulaRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves localized strings used by the built-in structural formatter.</summary>
/// <remarks>
/// A provider can be invoked concurrently and must remain thread-safe and
/// behaviorally stable for the processor or engine lifetime so immutable cached
/// documents do not depend on which caller wins a race. Processor invocations
/// use bounded process-wide callback admission; an uncooperative callback can
/// retire after the caller receives a timeout and holds its slot until then.
/// Direct use of <see cref="DefaultMathAccessibilityFormatter"/> and direct
/// delimiter scanning invoke the provider synchronously, so providers on those
/// paths must also be prompt and non-blocking.
/// </remarks>
public interface IMathStringProvider
{
    /// <summary>Returns a localized string, or null to use the built-in English fallback.</summary>
    string? GetString(string resourceKey, string? languageTag);
}

/// <summary>Stable keys requested from <see cref="IMathStringProvider"/>.</summary>
public static class MathStringKeys
{
    /// <summary>Resource key for the atomic formula automation name.</summary>
    public const string AutomationName = "Math.AutomationName";
    /// <summary>Resource key for the original-TeX help-text format.</summary>
    public const string OriginalTex = "Math.OriginalTex";
    /// <summary>Resource key for the fraction structural term.</summary>
    public const string Fraction = "Math.Fraction";
    /// <summary>Resource key for the numerator structural term.</summary>
    public const string Numerator = "Math.Numerator";
    /// <summary>Resource key for the denominator structural term.</summary>
    public const string Denominator = "Math.Denominator";
    /// <summary>Resource key for the end-fraction structural term.</summary>
    public const string EndFraction = "Math.EndFraction";
    /// <summary>Resource key for the square-root structural term.</summary>
    public const string SquareRoot = "Math.SquareRoot";
    /// <summary>Resource key for the end-root structural term.</summary>
    public const string EndRoot = "Math.EndRoot";
    /// <summary>Resource key for the superscript structural term.</summary>
    public const string Superscript = "Math.Superscript";
    /// <summary>Resource key for the end-superscript structural term.</summary>
    public const string EndSuperscript = "Math.EndSuperscript";
    /// <summary>Resource key for the subscript structural term.</summary>
    public const string Subscript = "Math.Subscript";
    /// <summary>Resource key for the end-subscript structural term.</summary>
    public const string EndSubscript = "Math.EndSubscript";
    /// <summary>Resource key for the addition operator term.</summary>
    public const string Plus = "Math.Plus";
    /// <summary>Resource key for the subtraction operator term.</summary>
    public const string Minus = "Math.Minus";
    /// <summary>Resource key for the equality operator term.</summary>
    public const string EqualsOperator = "Math.Equals";
    /// <summary>Resource key for the multiplication operator term.</summary>
    public const string Times = "Math.Times";
    /// <summary>Resource key for the division operator term.</summary>
    public const string DividedBy = "Math.DividedBy";
    /// <summary>Resource key for an invalid-formula diagnostic.</summary>
    public const string InvalidFormula = "Math.InvalidFormula";
    /// <summary>Resource key for an unmatched inline delimiter diagnostic.</summary>
    public const string UnmatchedInlineDelimiter = "Math.UnmatchedInlineDelimiter";
    /// <summary>Resource key for an unmatched display delimiter diagnostic.</summary>
    public const string UnmatchedDisplayDelimiter = "Math.UnmatchedDisplayDelimiter";
    /// <summary>Resource key for a source-length budget diagnostic.</summary>
    public const string SourceTooLong = "Math.SourceTooLong";
    /// <summary>Resource key for a nesting-depth budget diagnostic.</summary>
    public const string NestingTooDeep = "Math.NestingTooDeep";
    /// <summary>Resource key for a scene-complexity budget diagnostic.</summary>
    public const string SceneTooComplex = "Math.SceneTooComplex";
    /// <summary>Resource key for a scene-dimension budget diagnostic.</summary>
    public const string SceneTooLarge = "Math.SceneTooLarge";
    /// <summary>Resource key for a working-memory budget diagnostic.</summary>
    public const string WorkingMemoryExceeded = "Math.WorkingMemoryExceeded";
    /// <summary>Resource key for a processing-deadline diagnostic.</summary>
    public const string ProcessingTimedOut = "Math.ProcessingTimedOut";
    /// <summary>Resource key for an unexpected processing failure.</summary>
    public const string ProcessingFailed = "Math.ProcessingFailed";
}

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
