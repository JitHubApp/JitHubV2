namespace MarkdownRenderer.ScrollFidelityProbe.Capture;

/// <summary>Consecutive rows of the viewport that each hold a single color.</summary>
internal readonly record struct UniformRun(int Start, int Length);
