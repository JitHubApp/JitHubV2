using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Internal.Inlines.Runs;

internal sealed class CodeInlineRun : InlineRun
{
    private readonly string _text;
    public CodeInlineRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.CodeInline;
    }
    private CodeInlineRun(CodeInlineRun source) : base(source) => _text = source._text;
    internal CodeInlineRun CloneForPrivateRasterPlacement() => new(this);
    public override string Text => _text;
}
