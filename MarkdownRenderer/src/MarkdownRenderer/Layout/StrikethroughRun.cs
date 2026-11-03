using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

internal sealed class StrikethroughRun : InlineRun
{
    private readonly string _text;
    public StrikethroughRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Strikethrough;
    }
    private StrikethroughRun(StrikethroughRun source) : base(source) => _text = source._text;
    internal StrikethroughRun CloneForPrivateRasterPlacement() => new(this);
    public override string Text => _text;
}
