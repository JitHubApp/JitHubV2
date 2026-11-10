using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Internal.Inlines.Runs;

internal sealed class StrongRun : InlineRun
{
    private readonly string _text;
    public StrongRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Strong;
    }
    private StrongRun(StrongRun source) : base(source) => _text = source._text;
    internal StrongRun CloneForPrivateRasterPlacement() => new(this);
    public override string Text => _text;
}
