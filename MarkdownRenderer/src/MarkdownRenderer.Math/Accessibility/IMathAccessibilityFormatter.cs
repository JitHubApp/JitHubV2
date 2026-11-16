namespace MarkdownRenderer.Math.Accessibility;

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
