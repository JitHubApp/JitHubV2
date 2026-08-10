namespace MarkdownRenderer.Images;

/// <summary>Event payload for an image the renderer could not expose.</summary>
public sealed class MarkdownImageUnavailableEventArgs : System.EventArgs
{
    /// <summary>Initializes an unavailable-image event.</summary>
    public MarkdownImageUnavailableEventArgs(string source, MarkdownImageUnavailableReason reason)
    {
        Source = source ?? string.Empty;
        Reason = reason;
    }

    /// <summary>Gets the source string from the Markdown document.</summary>
    public string Source { get; }

    /// <summary>Gets the reason the image was not exposed.</summary>
    public MarkdownImageUnavailableReason Reason { get; }
}
