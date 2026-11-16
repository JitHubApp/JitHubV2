using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Layout.Internal.Inlines.Runs;

/// <summary>
/// A source-less decorative HTML image with explicit dimensions. The single
/// layout placeholder preserves source/character indexing, while semantic and
/// selectable text intentionally expose nothing. This run owns no image,
/// renderer, or asynchronous resource lifetime.
/// </summary>
internal sealed class InlineSpacerRun : InlineRun
{
    internal InlineSpacerRun(float width, float height)
    {
        if (!float.IsFinite(width) || width <= 0f || width > SafeHtmlParser.MaximumAbsoluteLength)
            throw new System.ArgumentOutOfRangeException(nameof(width));
        if (!float.IsFinite(height) || height <= 0f || height > SafeHtmlParser.MaximumAbsoluteLength)
            throw new System.ArgumentOutOfRangeException(nameof(height));

        Width = width;
        Height = height;
        RenderedLength = 1;
    }

    private InlineSpacerRun(InlineSpacerRun source)
        : base(source)
    {
        Width = source.Width;
        Height = source.Height;
    }

    internal float Width { get; }
    internal float Height { get; }

    internal InlineSpacerRun CloneForPrivateRasterPlacement() => new(this);

    public override string Text => InlineEmbedRun.PlaceholderChar;

    public override string AccessibleText => string.Empty;
}
