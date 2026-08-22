using System;
using System.Collections.Generic;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Layout;

internal sealed class SafeHtmlInlineState
{
    private readonly List<Scope> _scopes = [];

    public InlineRun? Process(HtmlInline html, MarkdownLayoutContext context)
    {
        var span = new SourceSpan(html.Span.Start, html.Span.Length);
        if (!SafeHtmlParser.TryParseSingleTag(html.Tag, out SafeHtmlTag tag))
        {
            return new TextRun(html.Tag) { SourceSpan = span };
        }

        if (tag.Kind is SafeHtmlTagKind.Comment or SafeHtmlTagKind.Declaration)
        {
            return null;
        }

        if (tag.Kind == SafeHtmlTagKind.Closing)
        {
            PopThrough(tag.Name);
            return null;
        }

        bool suppressed = IsSuppressed || SafeHtmlParser.IsSuppressedElement(tag.Name);
        string? styleKey = GetStyleKey(tag.Name);
        string? linkUrl = null;
        string? linkTitle = null;
        if (tag.Name == "a" && SafeHtmlParser.TryGetSafeLink(tag, out string safeLink))
        {
            linkUrl = safeLink;
            tag.TryGetAttribute("title", out linkTitle!);
        }

        if (tag.Kind == SafeHtmlTagKind.Opening)
        {
            _scopes.Add(new Scope(tag.Name, styleKey, linkUrl, linkTitle, suppressed));
        }

        if (suppressed)
        {
            return null;
        }

        if (tag.Name == "br")
        {
            return new LineBreakRun(isHard: true) { SourceSpan = span };
        }

        if (tag.Name == "hr")
        {
            return new LineBreakRun(isHard: true) { SourceSpan = span };
        }

        if (tag.Name != "img")
        {
            return null;
        }

        string alt = tag.TryGetAttribute("alt", out string altValue) ? altValue : "image";
        if (!SafeHtmlParser.TryGetSafeImageSource(tag, out string source))
        {
            return string.IsNullOrWhiteSpace(alt)
                ? null
                : Apply(new TextRun(alt) { SourceSpan = span });
        }

        tag.TryGetAttribute("title", out string title);
        SafeHtmlParser.TryGetLength(tag, "width", out SafeHtmlLength width);
        SafeHtmlParser.TryGetLength(tag, "height", out SafeHtmlLength height);
        Scope? link = FindLinkScope();
        return new InlineImageRun(
            context,
            string.IsNullOrWhiteSpace(alt) ? "image" : alt,
            source,
            string.IsNullOrWhiteSpace(title) ? null : title,
            link?.LinkUrl,
            link?.LinkTitle,
            width.Value > 0 ? width : null,
            height.Value > 0 ? height : null)
        {
            SourceSpan = span,
        };
    }

    public InlineRun? Apply(InlineRun? run)
    {
        if (run is null || IsSuppressed)
        {
            return null;
        }

        Scope? link = FindLinkScope();
        string? styleKey = FindStyleKey();
        if (run is LinkRun existingLink)
        {
            if (styleKey == Theming.MarkdownElementKeys.Superscript)
            {
                existingLink.IsSuperscript = true;
            }

            return existingLink;
        }

        if (link?.LinkUrl is { Length: > 0 } href && run is not InlineImageRun)
        {
            return new LinkRun(run.Text, href, link.LinkTitle)
            {
                SourceSpan = run.SourceSpan,
                IsSuperscript = styleKey == Theming.MarkdownElementKeys.Superscript,
            };
        }

        if (run is not TextRun || string.IsNullOrEmpty(styleKey))
        {
            return run;
        }

        InlineRun styled = styleKey switch
        {
            Theming.MarkdownElementKeys.CodeInline => new CodeInlineRun(run.Text),
            Theming.MarkdownElementKeys.Strong => new StrongRun(run.Text),
            Theming.MarkdownElementKeys.Emphasis => new EmphasisRun(run.Text),
            Theming.MarkdownElementKeys.Strikethrough => new StrikethroughRun(run.Text),
            Theming.MarkdownElementKeys.Subscript => new SubscriptRun(run.Text),
            Theming.MarkdownElementKeys.Superscript => new SuperscriptRun(run.Text),
            Theming.MarkdownElementKeys.Inserted => new InsertedRun(run.Text),
            Theming.MarkdownElementKeys.Marked => new MarkedRun(run.Text),
            _ => run,
        };
        styled.SourceSpan = run.SourceSpan;
        return styled;
    }

    private bool IsSuppressed
    {
        get
        {
            for (int index = _scopes.Count - 1; index >= 0; index--)
            {
                if (_scopes[index].Suppressed)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private Scope? FindLinkScope()
    {
        for (int index = _scopes.Count - 1; index >= 0; index--)
        {
            if (!string.IsNullOrWhiteSpace(_scopes[index].LinkUrl))
            {
                return _scopes[index];
            }
        }

        return null;
    }

    private string? FindStyleKey()
    {
        for (int index = _scopes.Count - 1; index >= 0; index--)
        {
            if (!string.IsNullOrWhiteSpace(_scopes[index].StyleKey))
            {
                return _scopes[index].StyleKey;
            }
        }

        return null;
    }

    private void PopThrough(string name)
    {
        for (int index = _scopes.Count - 1; index >= 0; index--)
        {
            if (!_scopes[index].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _scopes.RemoveRange(index, _scopes.Count - index);
            return;
        }
    }

    internal static string? GetStyleKey(string name) => name switch
    {
        "b" or "strong" => Theming.MarkdownElementKeys.Strong,
        "i" or "em" or "cite" or "var" => Theming.MarkdownElementKeys.Emphasis,
        "s" or "strike" or "del" => Theming.MarkdownElementKeys.Strikethrough,
        "sub" => Theming.MarkdownElementKeys.Subscript,
        "sup" => Theming.MarkdownElementKeys.Superscript,
        "ins" or "u" => Theming.MarkdownElementKeys.Inserted,
        "mark" => Theming.MarkdownElementKeys.Marked,
        "code" or "kbd" or "samp" => Theming.MarkdownElementKeys.CodeInline,
        _ => null,
    };

    private sealed record Scope(
        string Name,
        string? StyleKey,
        string? LinkUrl,
        string? LinkTitle,
        bool Suppressed);
}
