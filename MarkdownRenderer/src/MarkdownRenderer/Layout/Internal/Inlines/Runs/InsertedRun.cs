using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Internal.Inlines.Runs;

internal sealed class InsertedRun : InlineRun
{
    private readonly string _text;
    public InsertedRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Inserted;
    }
    private InsertedRun(InsertedRun source) : base(source) => _text = source._text;
    internal InsertedRun CloneForPrivateRasterPlacement() => new(this);
    public override string Text => _text;
}
