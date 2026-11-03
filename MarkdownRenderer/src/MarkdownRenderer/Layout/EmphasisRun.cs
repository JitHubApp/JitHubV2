using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

internal sealed class EmphasisRun : InlineRun
{
    private readonly string _text;
    public EmphasisRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Emphasis;
    }
    private EmphasisRun(EmphasisRun source) : base(source) => _text = source._text;
    internal EmphasisRun CloneForPrivateRasterPlacement() => new(this);
    public override string Text => _text;
}
