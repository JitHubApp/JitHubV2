namespace MarkdownRenderer.Images;

/// <summary>Event payload for an image the renderer could not expose.</summary>
public sealed class MarkdownImageUnavailableEventArgs : System.EventArgs
{
    /// <summary>Initializes an unavailable-image event.</summary>
    public MarkdownImageUnavailableEventArgs(string source, MarkdownImageUnavailableReason reason)
        : this(source, reason, null)
    {
    }

    /// <summary>Initializes an unavailable-image event with an SVG failure category, when applicable.</summary>
    public MarkdownImageUnavailableEventArgs(
        string source,
        MarkdownImageUnavailableReason reason,
        MarkdownSvgFailureReason? svgFailureReason)
    {
        Source = source ?? string.Empty;
        Reason = reason;
        SvgFailureReason = svgFailureReason;
    }

    /// <summary>Gets the source string from the Markdown document.</summary>
    public string Source { get; }

    /// <summary>Gets the reason the image was not exposed.</summary>
    public MarkdownImageUnavailableReason Reason { get; }

    /// <summary>Gets the SVG failure category, or null for non-SVG image failures.</summary>
    public MarkdownSvgFailureReason? SvgFailureReason { get; }
}
