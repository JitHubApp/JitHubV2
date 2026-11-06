using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Images;

/// <summary>
/// The title, description and intrinsic size of an SVG document, read from
/// its root element. The view uses them for the image's accessible name and
/// help text, and for its size before it is rendered.
/// </summary>
/// <remarks>
/// Create one with <see cref="MarkdownSvgSourceInfoReader"/> and pass it to
/// <see cref="MarkdownSvgSourcePreparation.Admit(byte[], MarkdownSvgSourceInfo)"/>.
/// </remarks>
public sealed class MarkdownSvgSourceInfo
{
    internal MarkdownSvgSourceInfo(SvgSourceMetadata metadata) => Metadata = metadata;

    internal SvgSourceMetadata Metadata { get; }

    /// <summary>Gets the root element's first <c>title</c> child, or null.</summary>
    public string? Title => Metadata.Title;

    /// <summary>Gets the root element's first <c>desc</c> child, or null.</summary>
    public string? Description => Metadata.Desc;

    /// <summary>
    /// Gets the width in DIPs from the root's <c>width</c>, <c>height</c> and
    /// <c>viewBox</c>, or 0 when they do not give one.
    /// </summary>
    public double IntrinsicWidth => Metadata.Geometry.IntrinsicWidth;

    /// <summary>
    /// Gets the height in DIPs from the root's <c>width</c>, <c>height</c> and
    /// <c>viewBox</c>, or 0 when they do not give one.
    /// </summary>
    public double IntrinsicHeight => Metadata.Geometry.IntrinsicHeight;
}
