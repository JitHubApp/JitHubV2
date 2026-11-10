using System;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Internal.Inlines.Runs;

internal sealed class LinkRun : InlineRun
{
    private readonly string _text;
    private readonly LinkInlineStyleMap? _inlineStyleMap;
    public readonly string Url;
    public readonly string? Title;
    public string? AccessibilityName { get; init; }
    public string? DisclosureId { get; init; }
    public bool IsDisclosure => !string.IsNullOrEmpty(DisclosureId);
    public bool IsExpanded { get; init; }
    /// <summary>
    /// When true, the run is rendered at a reduced size and raised baseline
    /// (like a superscript). Used for footnote citation markers [^1].
    /// </summary>
    public bool IsSuperscript { get; internal set; }
    public LinkRun(string text, string url, string? title = null)
        : this(text, url, title, inlineStyleMap: null)
    {
    }

    internal LinkRun(
        string text,
        string url,
        string? title,
        LinkInlineStyleMap? inlineStyleMap)
    {
        _text = text ?? string.Empty;
        Url = url ?? string.Empty;
        Title = title;
        _inlineStyleMap = inlineStyleMap;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Link;
    }

    private LinkRun(LinkRun source)
        : base(source)
    {
        _text = source._text;
        Url = source.Url;
        Title = source.Title;
        AccessibilityName = source.AccessibilityName;
        DisclosureId = source.DisclosureId;
        IsExpanded = source.IsExpanded;
        IsSuperscript = source.IsSuperscript;
        _inlineStyleMap = source._inlineStyleMap;
    }

    internal LinkInlineStyleMap? InlineStyleMap => _inlineStyleMap;

    internal LinkRun CloneForPrivateRasterPlacement()
    {
        if (IsDisclosure || IsExpanded)
            throw new InvalidOperationException("Stateful disclosure links cannot be cloned for raster placement.");
        return new LinkRun(this);
    }

    public override string Text => _text;
    public override string AccessibleText => string.IsNullOrWhiteSpace(AccessibilityName)
        ? _text
        : AccessibilityName;
}
