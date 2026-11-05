using System;

namespace JitHub.Markdown.Automation;

/// <summary>
/// Converts the lifecycle harness's requested text scale into the factor applied
/// to the renderer's font-size tokens.
/// </summary>
public static class MarkdownLifecycleTextScale
{
    /// <summary>
    /// Returns the token factor that yields <paramref name="requestedScale"/> once
    /// Windows applies its own text scale.
    /// </summary>
    /// <remarks>
    /// The fixture asks for a final effective scale. MarkdownEnvironmentMonitor
    /// applies the real Windows scale after these test-only font resources are
    /// resolved, so that scale is divided out. Both scales are clamped to 1–3,
    /// and a missing or invalid system scale counts as 1.
    /// </remarks>
    /// <param name="requestedScale">The effective scale the harness asks for.</param>
    /// <param name="observedSystemScale">The Windows text scale, or <see langword="null"/> when it could not be read.</param>
    public static double ToTokenScale(double requestedScale, double? observedSystemScale)
    {
        double systemScale = observedSystemScale is double observed && double.IsFinite(observed) && observed > 0
            ? Math.Clamp(observed, 1, 3)
            : 1;
        return Math.Clamp(requestedScale, 1, 3) / systemScale;
    }
}
