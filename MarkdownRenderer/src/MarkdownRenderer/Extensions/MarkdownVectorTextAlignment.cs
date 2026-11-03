namespace MarkdownRenderer.Extensions;

/// <summary>Font and positioning attributes for an already-resolved text line.</summary>
public enum MarkdownVectorTextAlignment
{
    /// <summary>The baseline origin is the leading edge.</summary>
    Start,
    /// <summary>The baseline origin is the visual center.</summary>
    Center,
    /// <summary>The baseline origin is the trailing edge.</summary>
    End,
}
