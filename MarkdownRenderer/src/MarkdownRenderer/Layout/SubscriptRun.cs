using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

internal sealed class SubscriptRun : InlineRun
{
    private readonly string _text;
    public SubscriptRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Subscript;
    }
    private SubscriptRun(SubscriptRun source) : base(source) => _text = source._text;
    internal SubscriptRun CloneForPrivateRasterPlacement() => new(this);
    public override string Text => _text;
}
