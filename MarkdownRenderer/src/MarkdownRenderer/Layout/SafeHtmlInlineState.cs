using System;
using System.Collections.Generic;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Tracks inline safe-HTML for one inline flow: the open element scope stack,
/// the script (subscript/superscript) ancestry, and the node and input budget.
/// It lowers each HTML tag to a run or a scope change and applies the open
/// scopes' links, styles and class aliases to runs inside them.
/// </summary>
internal sealed class SafeHtmlInlineState
{
    private readonly List<SafeHtmlInlineScope> _scopes = [];
    private List<SafeHtmlScriptStyleAncestryToken>? _scriptStyleAncestry;

    /// <summary>
    /// The open script scopes in source order, or null before the first one.
    /// </summary>
    internal IReadOnlyList<SafeHtmlScriptStyleAncestryToken>? ScriptStyleAncestry => _scriptStyleAncestry;

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

    internal SafeHtmlScriptStyleScope PushMarkdownScript(string styleKey)
    {
        if (!SafeHtmlInlineElementNames.IsScriptStyleKey(styleKey))
            return default;

        var token = new SafeHtmlScriptStyleAncestryToken(styleKey!);
        (_scriptStyleAncestry ??= []).Add(token);
        return new SafeHtmlScriptStyleScope(this, token);
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
            return ProcessSuppressedElement(_policy, tag, html, span);
        }

        if (!SafeHtmlInlineElementNames.IsSupportedElement(tag.Name))
        {
            return _policy.RenderUnknownElementsLiterally
                ? new TextRun(html.Tag) { SourceSpan = span }
                : null;
        }

        return ProcessSupportedElement(_policy, tag, context, span, containingLinkUrl, containingLinkTitle);
    }

    // A closing tag pops its scope; an opening tag pushes one carrying its
    // style key and any safe link before its run is created, unless an
    // enclosing element suppresses it.
    private InlineRun? ProcessSupportedElement(
        SafeHtmlRenderPolicy policy,
        in SafeHtmlTag tag,
        MarkdownLayoutContext context,
        SourceSpan span,
        string? containingLinkUrl,
        string? containingLinkTitle)
    {
        if (tag.Kind == SafeHtmlTagKind.Closing)
        {
            PopThrough(tag.Name);
            return null;
        }

        bool suppressed = IsSuppressed || SafeHtmlParser.IsSuppressedElement(tag.Name);
        string? styleKey = SafeHtmlInlineElementNames.GetStyleKey(tag.Name);
        string? linkUrl = null;
        string? linkTitle = null;
        if (policy.EnableLinks &&
            tag.Name == "a" &&
            SafeHtmlParser.TryGetSafeLink(tag, out string safeLink))
        {
            linkUrl = safeLink;
            tag.TryGetAttribute("title", out linkTitle!);
        }

        if (tag.Kind == SafeHtmlTagKind.Opening)
        {
            PushOpeningScope(tag, styleKey, linkUrl, linkTitle, suppressed);
        }

        if (suppressed)
        {
            return null;
        }

        return SafeHtmlInlineElementRunFactory.CreateElementRun(
            this, policy, tag, context, span, containingLinkUrl, containingLinkTitle);
    }

    private InlineRun? ProcessSuppressedElement(
        SafeHtmlRenderPolicy policy,
        in SafeHtmlTag tag,
        HtmlInline html,
        SourceSpan span)
    {
        if (policy.RenderUnknownElementsLiterally)
            return new TextRun(html.Tag) { SourceSpan = span };

        if (tag.Kind == SafeHtmlTagKind.Closing)
        {
            PopThrough(tag.Name);
        }
        else if (tag.Kind == SafeHtmlTagKind.Opening)
        {
            _scopes.Add(new SafeHtmlInlineScope(
                tag.Name,
                styleKey: null,
                linkUrl: null,
                linkTitle: null,
                suppressed: true,
                styleAliases: Array.Empty<string>()));
        }

        return null;
    }

    private void PushOpeningScope(
        in SafeHtmlTag tag,
        string? styleKey,
        string? linkUrl,
        string? linkTitle,
        bool suppressed)
    {
        SafeHtmlScriptStyleAncestryToken? scriptToken = null;
        if (SafeHtmlInlineElementNames.IsScriptStyleKey(styleKey))
        {
            scriptToken = new SafeHtmlScriptStyleAncestryToken(styleKey!);
            (_scriptStyleAncestry ??= []).Add(scriptToken);
        }

        _scopes.Add(new SafeHtmlInlineScope(
            tag.Name,
            styleKey,
            linkUrl,
            linkTitle,
            suppressed,
            GetClassAliases(tag),
            scriptToken));
    }

    public InlineRun? Apply(InlineRun? run)
    {
        if (run is null || IsSuppressed)
        {
            return null;
        }

        SafeHtmlInlineScope? link = FindLinkScope();
        string? styleKey = FindStyleKey();
        if (run is LinkRun existingLink)
        {
            if (styleKey == Theming.MarkdownElementKeys.Superscript)
            {
                bool wasSuperscript = existingLink.IsSuperscript;
                existingLink.IsSuperscript = true;
                existingLink.SafeHtmlScriptRoleIsRunElement |= !wasSuperscript;
            }

            return ApplyAliases(existingLink);
        }

        if (link?.LinkUrl is { Length: > 0 } href && run is not InlineImageRun and not InlineSpacerRun)
        {
            return ApplyAliases(new LinkRun(run.Text, href, link?.LinkTitle)
            {
                SourceSpan = run.SourceSpan,
                IsSuperscript = styleKey == Theming.MarkdownElementKeys.Superscript,
                SafeHtmlScriptRoleIsRunElement = styleKey == Theming.MarkdownElementKeys.Superscript,
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
        styled.SafeHtmlScriptRoleIsRunElement = SafeHtmlInlineElementNames.IsScriptStyleKey(styleKey);
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

    /// <summary>
    /// Returns the innermost open scope that carries a safe link, or null when
    /// no enclosing element contributes one.
    /// </summary>
    internal SafeHtmlInlineScope? FindLinkScope()
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

    /// <summary>
    /// Whether any open scope is an <c>a</c> element, whether or not its link
    /// was safe enough to keep.
    /// </summary>
    internal bool IsInsideLinkElement()
    {
        foreach (SafeHtmlInlineScope scope in _scopes)
        {
            if (scope.Name.Equals("a", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether any open scope is a <c>picture</c> element.
    /// </summary>
    internal bool IsInsidePicture()
    {
        foreach (SafeHtmlInlineScope scope in _scopes)
        {
            if (scope.Name.Equals("picture", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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

    /// <summary>
    /// Stamps <paramref name="run"/> with the class aliases of every open scope
    /// (outermost first, then the run's own) and with the open script ancestry,
    /// appending the run's own script role when the ancestry does not already
    /// end with it. Returns the same run.
    /// </summary>
    internal InlineRun ApplyAliases(InlineRun run)
    {
        int aliasCount = run.StyleAliases.Count;
        foreach (SafeHtmlInlineScope scope in _scopes)
            aliasCount += scope.StyleAliases.Count;

        string? runScriptRole = SafeHtmlInlineElementNames.IsScriptStyleKey(run.ElementKey)
            ? run.ElementKey
            : run is LinkRun { IsSuperscript: true }
                ? Theming.MarkdownElementKeys.Superscript
                : null;
        List<SafeHtmlScriptStyleAncestryToken>? scriptAncestry = _scriptStyleAncestry;
        bool runRoleAlreadyCaptured = run.SafeHtmlScriptRoleIsRunElement &&
            runScriptRole is not null &&
            scriptAncestry is { Count: > 0 } &&
            string.Equals(
                scriptAncestry[^1].StyleKey,
                runScriptRole,
                StringComparison.Ordinal);
        bool appendRunScriptRole = runScriptRole is not null && !runRoleAlreadyCaptured;
        int inheritedScriptAncestryCount = scriptAncestry?.Count ?? 0;
        int scriptAncestryCount = inheritedScriptAncestryCount + (appendRunScriptRole ? 1 : 0);

        if (aliasCount == 0 && scriptAncestryCount == 0)
            return run;

        if (aliasCount > 0)
        {
            var aliases = new string[aliasCount];
            int written = 0;
            foreach (SafeHtmlInlineScope scope in _scopes)
            {
                foreach (string alias in scope.StyleAliases)
                    aliases[written++] = alias;
            }
            foreach (string alias in run.StyleAliases)
                aliases[written++] = alias;
            run.SetStyleAliases(aliases);
        }

        if (scriptAncestryCount > 0)
        {
            var scriptStyleKeys = new string[scriptAncestryCount];
            if (scriptAncestry is { } inheritedAncestry)
            {
                for (int index = 0; index < inheritedScriptAncestryCount; index++)
                    scriptStyleKeys[index] = inheritedAncestry[index].StyleKey;
            }
            if (appendRunScriptRole)
                scriptStyleKeys[^1] = runScriptRole!;
            run.ScriptStyleAncestryKeys = scriptStyleKeys;
        }

        return run;
    }

    private void PopThrough(string name)
    {
        for (int index = _scopes.Count - 1; index >= 0; index--)
        {
            if (!_scopes[index].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            for (int removedIndex = _scopes.Count - 1; removedIndex >= index; removedIndex--)
                RemoveScriptToken(_scopes[removedIndex].ScriptToken);
            _scopes.RemoveRange(index, _scopes.Count - index);
            return;
        }
    }

    /// <summary>
    /// Removes the script ancestry entry identified by <paramref name="token"/>
    /// when it is still open; a null token or an already removed one is a no-op.
    /// </summary>
    internal void RemoveScriptToken(SafeHtmlScriptStyleAncestryToken? token)
    {
        List<SafeHtmlScriptStyleAncestryToken>? ancestry = _scriptStyleAncestry;
        if (token is null || ancestry is null)
            return;

        int index = ancestry.LastIndexOf(token);
        if (index >= 0)
            ancestry.RemoveAt(index);
    }
}
