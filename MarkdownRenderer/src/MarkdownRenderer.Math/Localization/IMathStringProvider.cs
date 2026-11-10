using MarkdownRenderer.Math.Accessibility;

namespace MarkdownRenderer.Math.Localization;

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
