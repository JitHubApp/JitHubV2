using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

internal sealed class MarkedRun : InlineRun
{
    private readonly string _text;
    public MarkedRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Marked;
    }
    private MarkedRun(MarkedRun source) : base(source) => _text = source._text;
    internal MarkedRun CloneForPrivateRasterPlacement() => new(this);
    public override string Text => _text;
}
