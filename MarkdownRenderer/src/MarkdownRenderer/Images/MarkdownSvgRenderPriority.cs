namespace MarkdownRenderer.Images;

/// <summary>Host scheduling class for work that has not entered the worker.</summary>
public enum MarkdownSvgRenderPriority
{
    /// <summary>Content in the overscan region that may wait behind visible work.</summary>
    Overscan,

    /// <summary>Content intersecting the viewport or otherwise needed immediately.</summary>
    Visible,
}
