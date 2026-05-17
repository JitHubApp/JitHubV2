using System;
using System.Collections.Generic;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

/// <summary>
/// One inline segment inside a paragraph or heading. Inline runs do not lay
/// themselves out individually; the parent block joins them into a single
/// CanvasTextLayout with per-range style spans.
/// </summary>
internal abstract class InlineRun
{
    public int InlineIndex { get; internal set; }
    public int RenderedLength { get; protected set; }
    public SourceSpan SourceSpan { get; init; }
    /// <summary>
    /// Empty string means "inherit container's style". Set to a specific key
    /// (e.g. <see cref="MarkdownElementKeys.Strong"/>) to apply a delta override.
    /// </summary>
    public string ElementKey { get; init; } = string.Empty;

    /// <summary>
    /// Attribute-derived style aliases (for example <c>.warning</c> or
    /// <c>#intro</c>) captured from the layout context when this run is built.
    /// </summary>
    public IReadOnlyList<string> StyleAliases { get; internal set; } = Array.Empty<string>();

    public void SetStyleAliases(IReadOnlyList<string> styleAliases)
        => StyleAliases = styleAliases ?? Array.Empty<string>();

    /// <summary>The text contributed by this run to the inline buffer.</summary>
    public abstract string Text { get; }

    /// <summary>
    /// Text exposed through UI Automation. Most runs expose their rendered text;
    /// visual atomic runs such as images can expose richer fallback text.
    /// </summary>
    public virtual string AccessibleText => Text;
}

internal sealed class TextRun : InlineRun
{
    private readonly string _text;
    public TextRun(string text) { _text = text ?? string.Empty; RenderedLength = _text.Length; }
    public override string Text => _text;
}

internal sealed class CodeInlineRun : InlineRun
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

internal sealed class StrongRun : InlineRun
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

internal sealed class EmphasisRun : InlineRun
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

internal sealed class StrikethroughRun : InlineRun
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

internal sealed class SubscriptRun : InlineRun
{
    private readonly string _text;
    public SubscriptRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Subscript;
    }
    public override string Text => _text;
}

internal sealed class SuperscriptRun : InlineRun
{
    private readonly string _text;
    public SuperscriptRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Superscript;
    }
    public override string Text => _text;
}

internal sealed class InsertedRun : InlineRun
{
    private readonly string _text;
    public InsertedRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Inserted;
    }
    public override string Text => _text;
}

internal sealed class MarkedRun : InlineRun
{
    private readonly string _text;
    public MarkedRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Marked;
    }
    public override string Text => _text;
}

internal sealed class AbbreviationRun : InlineRun
{
    private readonly string _text;
    public string Expansion { get; }

    public AbbreviationRun(string text, string expansion)
    {
        _text = text ?? string.Empty;
        Expansion = expansion ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Abbreviation;
    }

    public override string Text => _text;

    public override string AccessibleText =>
        string.IsNullOrWhiteSpace(Expansion) ? _text : $"{_text} ({Expansion})";
}

internal sealed class LinkRun : InlineRun
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

internal sealed class InlineImageRun : InlineRun
{
    public string Url { get; }
    public string? Title { get; }
    public string AltText { get; }
    public Boxes.ImageBox Image { get; }
    internal float DesiredWidth { get; private set; }
    internal float DesiredHeight { get; private set; }

    public InlineImageRun(MarkdownLayoutContext context, string altText, string url, string? title = null)
    {
        AltText = altText ?? string.Empty;
        Url = url ?? string.Empty;
        Title = title;
        Image = new Boxes.ImageBox(context, Url, AltText)
        {
            Margin = default
        };
        DesiredWidth = 24f;
        DesiredHeight = 24f;
        RenderedLength = 1;
    }

    public override string Text => InlineEmbedRun.PlaceholderChar;

    public override string AccessibleText => string.IsNullOrWhiteSpace(AltText) ? "image" : AltText;

    internal void Measure(float maxWidth, float lineHeight)
    {
        var size = Image.MeasureInline(maxWidth, lineHeight);
        DesiredWidth = Math.Max(1f, (float)size.Width);
        DesiredHeight = Math.Max(1f, (float)size.Height);
    }
}

internal sealed class LineBreakRun : InlineRun
{
    public LineBreakRun() { RenderedLength = 1; }
    public override string Text => "\n";
}
