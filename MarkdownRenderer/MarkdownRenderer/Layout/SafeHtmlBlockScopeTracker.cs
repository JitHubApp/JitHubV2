using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Layout;

internal sealed class SafeHtmlBlockScopeTracker
{
    private readonly List<Scope> _scopes = [];
    private readonly SafeHtmlParseLimits _limits;
    private long _inputLength;
    private int _nodeCount;
    private bool _suppressContentAfterBudget;

    internal SafeHtmlBlockScopeTracker(SafeHtmlParseLimits? limits = null)
        => _limits = limits ?? SafeHtmlParseLimits.Default;

    internal bool BudgetExceeded { get; private set; }

    /// <summary>
    /// Creates the independent block-stream tracker used by a nested builder.
    /// It inherits open tag/suppression state, but keeps a fresh work counter as
    /// before. A parent denial is sticky so nesting cannot re-enable HTML work.
    /// </summary>
    internal SafeHtmlBlockScopeTracker CloneForNestedContent()
    {
        var clone = new SafeHtmlBlockScopeTracker(_limits);
        clone.BudgetExceeded = BudgetExceeded;
        clone._suppressContentAfterBudget = _suppressContentAfterBudget;
        clone._scopes.AddRange(_scopes);
        return clone;
    }

    public SafeHtmlAlignment CurrentAlignment
    {
        get
        {
            for (int index = _scopes.Count - 1; index >= 0; index--)
            {
                if (_scopes[index].Alignment != SafeHtmlAlignment.Inherit)
                {
                    return _scopes[index].Alignment;
                }
            }

            return SafeHtmlAlignment.Inherit;
        }
    }

    public bool IsContentSuppressed
    {
        get
        {
            if (_suppressContentAfterBudget)
                return true;

            int summaryIndex = _scopes.FindLastIndex(static scope => scope.Name == "summary");
            if (summaryIndex > 0)
            {
                int ownerDetailsIndex = _scopes.FindLastIndex(
                    summaryIndex - 1,
                    static scope => scope.Name == "details");
                if (ownerDetailsIndex >= 0)
                {
                    // A closed disclosure still exposes the complete summary,
                    // including Markdown lists and code blocks. Only a closed
                    // ancestor disclosure outside that summary can hide it.
                    for (int index = 0; index < ownerDetailsIndex; index++)
                    {
                        if (_scopes[index].SuppressContent)
                            return true;
                    }

                    return false;
                }
            }

            return _scopes.Exists(static scope => scope.SuppressContent);
        }
    }

    /// <summary>
    /// Source identity of the open div/center flow containing the current
    /// block. Markdig may split one HTML flow at blank lines; preserving this
    /// identity lets layout join only its inline fragments without collapsing
    /// separate HTML paragraphs.
    /// </summary>
    internal int? CurrentInlineFlowScopeId
    {
        get
        {
            for (int index = _scopes.Count - 1; index >= 0; index--)
            {
                if (_scopes[index].InlineFlowScopeId is int scopeId)
                    return scopeId;
            }

            return null;
        }
    }

    public bool Process(
        string? html,
        int sourceOffset,
        IReadOnlyDictionary<string, bool> disclosureStates,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BudgetExceeded)
            return false;
        _inputLength += html?.Length ?? 0;
        if (_inputLength > _limits.MaxInputLength)
            return ExceedBudget();

        bool isTagSequence = SafeHtmlParser.TryParseTagSequence(
            html,
            _limits,
            cancellationToken,
            out IReadOnlyList<SafeHtmlTag> sequence,
            out bool sequenceScanBudgetExceeded);
        if (sequenceScanBudgetExceeded)
            return ExceedBudget();

        bool tagScanBudgetExceeded = false;
        IReadOnlyList<SafeHtmlTag> tags = isTagSequence
            ? sequence
            : SafeHtmlParser.ParseTags(
                html,
                _limits,
                cancellationToken,
                out tagScanBudgetExceeded);
        if (!isTagSequence && tagScanBudgetExceeded)
            return ExceedBudget();
        if (tags.Count == 0)
        {
            return false;
        }

        bool containsDetailsOpening = false;
        bool allTagsAreScopeTags = true;
        foreach (SafeHtmlTag tag in tags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_nodeCount >= _limits.MaxNodeCount)
                return ExceedBudget();
            _nodeCount++;
            allTagsAreScopeTags &= tag.Name is ("div" or "p" or "center" or "details" or "summary");
            if (tag.Kind == SafeHtmlTagKind.Closing)
            {
                if (tag.Name is "details" or "summary" or "div" or "p" or "center")
                {
                    PopThrough(tag.Name);
                }

                continue;
            }

            if (tag.Kind != SafeHtmlTagKind.Opening)
            {
                continue;
            }

            if (_scopes.Count >= _limits.MaxNestingDepth)
                return ExceedBudget();

            if (tag.Name == "details")
            {
                containsDetailsOpening = true;
                string id = (sourceOffset + tag.SourceStart).ToString(System.Globalization.CultureInfo.InvariantCulture);
                bool defaultExpanded = tag.TryGetAttribute("open", out _);
                bool expanded = disclosureStates.TryGetValue(id, out bool state) ? state : defaultExpanded;
                _scopes.Add(new Scope(tag.Name, SafeHtmlAlignment.Inherit, suppressContent: !expanded));
                continue;
            }

            if (tag.Name == "summary")
            {
                _scopes.Add(new Scope(tag.Name, SafeHtmlAlignment.Inherit, suppressContent: false));
                continue;
            }

            if (tag.Name is "div" or "p" or "center")
            {
                SafeHtmlAlignment alignment = tag.Name == "center"
                    ? SafeHtmlAlignment.Center
                    : SafeHtmlParser.GetAlignment(tag);
                int? inlineFlowScopeId = tag.Name is "div" or "center"
                    ? sourceOffset + tag.SourceStart
                    : null;
                _scopes.Add(new Scope(
                    tag.Name,
                    alignment,
                    suppressContent: false,
                    inlineFlowScopeId: inlineFlowScopeId));
            }
        }

        return isTagSequence && !containsDetailsOpening && allTagsAreScopeTags;
    }

    /// <summary>
    /// Observes HTML tags that Markdig placed inside an ordinary leaf block.
    /// CommonMark represents closures such as <c>&lt;/b&gt;&lt;/details&gt;</c>
    /// this way even when the matching <c>&lt;details&gt;</c> opener is an
    /// <see cref="Markdig.Syntax.HtmlBlock"/>. Without sharing those closing
    /// transitions with the block tracker, collapsed scopes accumulate until
    /// the depth budget is exhausted and later hidden content can become
    /// visible.
    /// </summary>
    internal void ObserveInlineTags(
        ContainerInline? container,
        IReadOnlyDictionary<string, bool> disclosureStates,
        CancellationToken cancellationToken = default)
    {
        if (container is null || BudgetExceeded)
            return;

        foreach (Inline inline in container)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inline is HtmlInline html)
            {
                Process(html.Tag, html.Span.Start, disclosureStates, cancellationToken);
            }

            if (inline is ContainerInline nested)
            {
                ObserveInlineTags(nested, disclosureStates, cancellationToken);
            }

            if (BudgetExceeded)
                return;
        }
    }

    /// <summary>
    /// Observes HTML transitions nested below a container block. Markdig can
    /// place a closing tag for an outer HTML scope inside a list item, quote,
    /// or another container even though the matching opener is top-level.
    /// The outer tracker must see those descendants as one source stream.
    /// </summary>
    internal void ObserveBlockTags(
        Block block,
        IReadOnlyDictionary<string, bool> disclosureStates,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BudgetExceeded)
            return;

        if (block is HtmlBlock html)
        {
            Process(html.Lines.ToString(), html.Span.Start, disclosureStates, cancellationToken);
            return;
        }

        if (block is LeafBlock { Inline: { } inline })
        {
            ObserveInlineTags(inline, disclosureStates, cancellationToken);
            return;
        }

        if (block is not ContainerBlock container)
            return;

        foreach (Block child in container)
        {
            ObserveBlockTags(child, disclosureStates, cancellationToken);
            if (BudgetExceeded)
                return;
        }
    }

    private bool ExceedBudget()
    {
        BudgetExceeded = true;
        if (_scopes.Exists(static scope => scope.Name == "details" && scope.SuppressContent))
            _suppressContentAfterBudget = true;

        // Keep disclosure state after denial so a hidden details body cannot
        // become visible just because its closing tag was beyond the budget.
        // Alignment scopes are discarded: once the tag stream is denied they
        // must not seed a nested renderer with a partially scanned alignment.
        for (int index = _scopes.Count - 1; index >= 0; index--)
        {
            if (_scopes[index].Name is not ("details" or "summary"))
                _scopes.RemoveAt(index);
        }
        return false;
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

    private readonly struct Scope
    {
        internal Scope(
            string name,
            SafeHtmlAlignment alignment,
            bool suppressContent,
            int? inlineFlowScopeId = null)
        {
            Name = name;
            Alignment = alignment;
            SuppressContent = suppressContent;
            InlineFlowScopeId = inlineFlowScopeId;
        }

        internal readonly string Name;
        internal readonly SafeHtmlAlignment Alignment;
        internal readonly bool SuppressContent;
        internal readonly int? InlineFlowScopeId;
    }
}
