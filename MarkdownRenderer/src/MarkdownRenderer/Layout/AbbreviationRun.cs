using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

internal sealed class AbbreviationRun : InlineRun
{
    private readonly string _text;
    public readonly string Expansion;

    public AbbreviationRun(string text, string expansion)
    {
        _text = text ?? string.Empty;
        Expansion = expansion ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Abbreviation;
    }

    private AbbreviationRun(AbbreviationRun source) : base(source)
    {
        _text = source._text;
        Expansion = source.Expansion;
    }

    internal AbbreviationRun CloneForPrivateRasterPlacement() => new(this);

    public override string Text => _text;

    public override string AccessibleText =>
        string.IsNullOrWhiteSpace(Expansion) ? _text : $"{_text} ({Expansion})";
}
