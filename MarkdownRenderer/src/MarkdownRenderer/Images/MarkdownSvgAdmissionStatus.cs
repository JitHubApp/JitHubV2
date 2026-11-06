namespace MarkdownRenderer.Images;

/// <summary>The outcome of <see cref="IMarkdownSvgAdmission.TryOpenAsync"/>.</summary>
public enum MarkdownSvgAdmissionStatus
{
    /// <summary>A default, uninitialized result. Views treat it as malformed.</summary>
    Invalid,
    /// <summary>Not enough capacity is free; wait and try again.</summary>
    CapacityUnavailable,
    /// <summary>The document is open and the caller owns it.</summary>
    Opened,
}
