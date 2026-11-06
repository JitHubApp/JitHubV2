namespace MarkdownRenderer.Math;

/// <summary>The accessibility description and timeout diagnostic reported when a formula misses its deadline.</summary>
internal readonly record struct MathEmergencyLocalizationSnapshot(
    MathAccessibilityDescription Accessibility,
    MathFormulaDiagnostic TimeoutDiagnostic,
    string TexPreview);
