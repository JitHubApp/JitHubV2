using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Internal.Inlines.Runs;

internal sealed class SuperscriptRun : InlineRun
{
    private readonly string _text;
    public SuperscriptRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Superscript;
    }
    private SuperscriptRun(SuperscriptRun source) : base(source) => _text = source._text;
    internal SuperscriptRun CloneForPrivateRasterPlacement() => new(this);
    public override string Text => _text;
}
