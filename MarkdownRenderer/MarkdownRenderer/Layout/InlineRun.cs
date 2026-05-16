using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

/// <summary>
/// One inline segment inside a paragraph or heading. Inline runs do not lay
/// themselves out individually; the parent block joins them into a single
/// CanvasTextLayout with per-range style spans.
/// </summary>
public abstract class InlineRun
{
    public int InlineIndex { get; internal set; }
    public int RenderedLength { get; protected set; }
    public SourceSpan SourceSpan { get; init; }
    /// <summary>
    /// Empty string means "inherit container's style". Set to a specific key
    /// (e.g. <see cref="MarkdownElementKeys.Strong"/>) to apply a delta override.
    /// </summary>
    public string ElementKey { get; init; } = string.Empty;

    /// <summary>The text contributed by this run to the inline buffer.</summary>
    public abstract string Text { get; }
}

public sealed class TextRun : InlineRun
{
    private readonly string _text;
    public TextRun(string text) { _text = text ?? string.Empty; RenderedLength = _text.Length; }
    public override string Text => _text;
}

public sealed class CodeInlineRun : InlineRun
{
    private readonly string _text;
    public CodeInlineRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.CodeInline;
    }
    public override string Text => _text;
}

public sealed class StrongRun : InlineRun
{
    private readonly string _text;
    public StrongRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Strong;
    }
    public override string Text => _text;
}

public sealed class EmphasisRun : InlineRun
{
    private readonly string _text;
    public EmphasisRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Emphasis;
    }
    public override string Text => _text;
}

public sealed class StrikethroughRun : InlineRun
{
    private readonly string _text;
    public StrikethroughRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Strikethrough;
    }
    public override string Text => _text;
}

public sealed class LinkRun : InlineRun
{
    private readonly string _text;
    public string Url { get; }
    public string? Title { get; }
    /// <summary>
    /// When true, the run is rendered at a reduced size and raised baseline
    /// (like a superscript). Used for footnote citation markers [^1].
    /// </summary>
    public bool IsSuperscript { get; init; }
    public LinkRun(string text, string url, string? title = null)
    {
        _text = text ?? string.Empty;
        Url = url ?? string.Empty;
        Title = title;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Link;
    }
    public override string Text => _text;
}

public sealed class InlineImageRun : InlineRun
{
    private readonly string _text;
    public string Url { get; }
    public string? Title { get; }
    public string AltText => _text;

    public InlineImageRun(string altText, string url, string? title = null)
    {
        _text = altText ?? string.Empty;
        Url = url ?? string.Empty;
        Title = title;
        RenderedLength = _text.Length;
    }

    public override string Text => _text;
}

public sealed class LineBreakRun : InlineRun
{
    public LineBreakRun() { RenderedLength = 1; }
    public override string Text => "\n";
}
