using System;
using System.Collections.Generic;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Layout;

internal sealed class SafeHtmlInlineState
{
    private readonly List<Scope> _scopes = [];
    private readonly SafeHtmlRenderPolicy? _policy;
    private int? _sourceStart;
    private int _nodeCount;
    private long _inputLength;

    internal bool BudgetExceeded { get; private set; }

    // Bound the source extent and retained leaf text, including legacy parsers
    // without precise spans. Containers are counted as nodes, not duplicate text.
    // Ordinary Markdown before the first HTML tag does not spend an HTML budget.
    internal bool TryAcceptNode(Inline node)
    {
        if (_policy is null)
            return true;
        if (BudgetExceeded)
            return false;
        if (_sourceStart is null && node is HtmlInline)
            _sourceStart = node.Span.Start;
        if (_sourceStart is not { } start)
            return true;

        // Legacy parser configurations need not enable precise source spans.
        // Retained literal lengths still enforce the ceiling on those paths.
        _inputLength += node switch
        {
            HtmlInline html => html.Tag.Length,
            LiteralInline literal => literal.Content.Length,
            CodeInline code => code.Content.Length,
            LineBreakInline => 1,
            ContainerInline => 0,
            _ => System.Math.Max(0, node.Span.Length),
        };
        if (_inputLength > _policy.Limits.MaxInputLength ||
            (long)node.Span.End - start + 1 > _policy.Limits.MaxInputLength ||
            _nodeCount >= _policy.Limits.MaxNodeCount)
        {
            BudgetExceeded = true;
            return false;
        }
        _nodeCount++;
        return true;
    }

    internal SafeHtmlInlineState(SafeHtmlRenderPolicy? policy)
    {
        _policy = policy;
    }

    internal bool IsStandaloneImage(HtmlInline html, MarkdownLayoutContext context)
    {
        if (_policy is null || html.Tag.Length > _policy.Limits.MaxTagLength)
            return false;

        return SafeHtmlParser.TryParseSingleTag(
                   html.Tag,
                   _policy.Limits,
                   context.CancellationToken,
                   out SafeHtmlTag tag,
                   out _) &&
               tag.Name == "img" &&
               tag.Kind is SafeHtmlTagKind.Opening or SafeHtmlTagKind.SelfClosing;
    }

    public InlineRun? Process(
        HtmlInline html,
        MarkdownLayoutContext context,
        string? containingLinkUrl = null,
        string? containingLinkTitle = null,
        SourceSpan? containingSourceSpan = null)
    {
        SourceSpan span = containingSourceSpan ?? new SourceSpan(html.Span.Start, html.Span.Length);
        if (_policy is null)
        {
            return new TextRun(html.Tag) { SourceSpan = span };
        }

        if (html.Tag.Length > _policy.Limits.MaxTagLength)
        {
            BudgetExceeded = true;
            return null;
        }

        if (!SafeHtmlParser.TryParseSingleTag(
                html.Tag,
                _policy.Limits,
                context.CancellationToken,
                out SafeHtmlTag tag,
                out bool scanBudgetExceeded))
        {
            if (scanBudgetExceeded)
            {
                BudgetExceeded = true;
                return null;
            }

            return new TextRun(html.Tag) { SourceSpan = span };
        }

        if (tag.Kind is SafeHtmlTagKind.Comment or SafeHtmlTagKind.Declaration)
        {
            return null;
        }

        if (tag.Kind == SafeHtmlTagKind.Opening &&
            _scopes.Count >= _policy.Limits.MaxNestingDepth)
        {
            BudgetExceeded = true;
            return null;
        }

        if (SafeHtmlParser.IsSuppressedElement(tag.Name))
        {
            if (_policy.RenderUnknownElementsLiterally)
                return new TextRun(html.Tag) { SourceSpan = span };

            if (tag.Kind == SafeHtmlTagKind.Closing)
            {
                PopThrough(tag.Name);
            }
            else if (tag.Kind == SafeHtmlTagKind.Opening)
            {
                _scopes.Add(new Scope(
                    tag.Name,
                    StyleKey: null,
                    LinkUrl: null,
                    LinkTitle: null,
                    Suppressed: true,
                    StyleAliases: Array.Empty<string>()));
            }

            return null;
        }

        if (!IsSupportedElement(tag.Name))
        {
            return _policy.RenderUnknownElementsLiterally
                ? new TextRun(html.Tag) { SourceSpan = span }
                : null;
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
        if (_policy.EnableLinks &&
            tag.Name == "a" &&
            SafeHtmlParser.TryGetSafeLink(tag, out string safeLink))
        {
            linkUrl = safeLink;
            tag.TryGetAttribute("title", out linkTitle!);
        }

        if (tag.Kind == SafeHtmlTagKind.Opening)
        {
            _scopes.Add(new Scope(
                tag.Name,
                styleKey,
                linkUrl,
                linkTitle,
                suppressed,
                GetClassAliases(tag)));
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

        bool hasExplicitAlt = tag.TryGetAttribute("alt", out string altValue);
        string alt = hasExplicitAlt
            ? altValue
            : context.ResolveString(MarkdownStringKeys.ImageName, MarkdownLocalizedStrings.ImageName);
        if (!_policy.EnableImages)
        {
            InlineRun? fallback = string.IsNullOrWhiteSpace(alt)
                ? null
                : Apply(new TextRun(alt) { SourceSpan = span });
            return ApplyContainingLink(fallback, containingLinkUrl, containingLinkTitle);
        }

        if (!SafeHtmlParser.TryGetSafeImageSource(tag, out string source))
        {
            InlineRun? fallback = string.IsNullOrWhiteSpace(alt)
                ? null
                : Apply(new TextRun(alt) { SourceSpan = span });
            return ApplyContainingLink(fallback, containingLinkUrl, containingLinkTitle);
        }

        tag.TryGetAttribute("title", out string title);
        SafeHtmlParser.TryGetLength(tag, "width", out SafeHtmlLength width);
        SafeHtmlParser.TryGetLength(tag, "height", out SafeHtmlLength height);
        Scope? link = FindLinkScope();
        return ApplyAliases(new InlineImageRun(
            context,
            alt,
            source,
            string.IsNullOrWhiteSpace(title) ? null : title,
            _policy.EnableLinks ? containingLinkUrl ?? link?.LinkUrl : null,
            _policy.EnableLinks ? containingLinkTitle ?? link?.LinkTitle : null,
            width.Value > 0 ? width : null,
            height.Value > 0 ? height : null)
        {
            SourceSpan = span,
        });
    }

    private InlineRun? ApplyContainingLink(
        InlineRun? run,
        string? containingLinkUrl,
        string? containingLinkTitle)
    {
        if (run is null ||
            !_policy!.EnableLinks ||
            string.IsNullOrWhiteSpace(containingLinkUrl) ||
            run is LinkRun)
        {
            return run;
        }

        return ApplyAliases(new LinkRun(run.Text, containingLinkUrl, containingLinkTitle)
        {
            SourceSpan = run.SourceSpan,
        });
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

            return ApplyAliases(existingLink);
        }

        if (link?.LinkUrl is { Length: > 0 } href && run is not InlineImageRun)
        {
            return ApplyAliases(new LinkRun(run.Text, href, link.LinkTitle)
            {
                SourceSpan = run.SourceSpan,
                IsSuperscript = styleKey == Theming.MarkdownElementKeys.Superscript,
            });
        }

        if (run is not TextRun || string.IsNullOrEmpty(styleKey))
        {
            return ApplyAliases(run);
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
        return ApplyAliases(styled);
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

    private IReadOnlyList<string> GetClassAliases(SafeHtmlTag tag)
    {
        if (!tag.TryGetAttribute("class", out string value) || string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        var aliases = new List<string>();
        foreach (string token in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (_policy!.IsStyleClassAllowed(token))
                aliases.Add(Theming.MarkdownElementKeys.Class(token));
        }

        return aliases.Count == 0 ? Array.Empty<string>() : aliases.ToArray();
    }

    private InlineRun ApplyAliases(InlineRun run)
    {
        int aliasCount = run.StyleAliases.Count;
        foreach (Scope scope in _scopes)
            aliasCount += scope.StyleAliases.Count;
        if (aliasCount == 0)
            return run;

        var aliases = new string[aliasCount];
        int written = 0;
        foreach (Scope scope in _scopes)
        {
            foreach (string alias in scope.StyleAliases)
                aliases[written++] = alias;
        }
        foreach (string alias in run.StyleAliases)
            aliases[written++] = alias;
        run.SetStyleAliases(aliases);
        return run;
    }

    private static bool IsSupportedElement(string name) => name is
        "a" or "address" or "article" or "aside" or "b" or "blockquote" or "br" or
        "caption" or "center" or "cite" or "code" or "col" or "colgroup" or "del" or
        "details" or "div" or "em" or "figcaption" or "figure" or "footer" or "h1" or
        "h2" or "h3" or "h4" or "h5" or "h6" or "header" or "hr" or "i" or "img" or
        "ins" or "kbd" or "li" or "main" or "mark" or "nav" or "ol" or "p" or
        "picture" or "pre" or "s" or "samp" or "section" or "small" or "source" or
        "span" or "strike" or "strong" or "sub" or "summary" or "sup" or "table" or
        "tbody" or "td" or "tfoot" or "th" or "thead" or "tr" or "u" or "ul" or "var";

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
        bool Suppressed,
        IReadOnlyList<string> StyleAliases);
}
