using MarkdownRenderer.Math.Accessibility;
using MarkdownRenderer.Math.Internal.Processing;

namespace MarkdownRenderer.Math.Localization.Internal;

/// <summary>The accessibility description and timeout diagnostic reported when a formula misses its deadline.</summary>
internal readonly record struct MathEmergencyLocalizationSnapshot(
    MathAccessibilityDescription Accessibility,
    MathFormulaDiagnostic TimeoutDiagnostic,
    string TexPreview);
