using System;

namespace MarkdownRenderer.Images;

/// <summary>
/// Indicates that an offscreen image source needs more than the speculative
/// byte ceiling. It is a deferral, not an image failure; a visible request
/// will retry with the visible ceiling.
/// </summary>
public sealed class MarkdownImageSourceDeferredException : Exception
{
    /// <summary>Creates a privacy-safe speculative deferral without source details.</summary>
    public MarkdownImageSourceDeferredException()
        : base("The image source exceeds the speculative byte reservation.")
    {
    }
}
