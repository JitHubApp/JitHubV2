namespace MarkdownRenderer.Math.Accessibility;

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
