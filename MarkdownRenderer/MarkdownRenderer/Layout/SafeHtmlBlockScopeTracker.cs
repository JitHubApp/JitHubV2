using System;
using System.Collections.Generic;
using System.Linq;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Layout;

internal sealed class SafeHtmlBlockScopeTracker
{
    private readonly List<Scope> _scopes = [];

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

    public bool IsContentSuppressed => _scopes.Exists(static scope => scope.SuppressContent);

    public bool Process(
        string? html,
        int sourceOffset,
        IReadOnlyDictionary<string, bool> disclosureStates)
    {
        bool isTagSequence = SafeHtmlParser.TryParseTagSequence(html, out IReadOnlyList<SafeHtmlTag> sequence);
        IReadOnlyList<SafeHtmlTag> tags = isTagSequence ? sequence : SafeHtmlParser.ParseTags(html);
        if (tags.Count == 0)
        {
            return false;
        }

        bool containsDetailsOpening = false;
        foreach (SafeHtmlTag tag in tags)
        {
            if (tag.Kind == SafeHtmlTagKind.Closing)
            {
                if (tag.Name is "details" or "div" or "p" or "center")
                {
                    PopThrough(tag.Name);
                }

                continue;
            }

            if (tag.Kind != SafeHtmlTagKind.Opening)
            {
                continue;
            }

            if (tag.Name == "details")
            {
                containsDetailsOpening = true;
                string id = (sourceOffset + tag.SourceStart).ToString(System.Globalization.CultureInfo.InvariantCulture);
                bool defaultExpanded = tag.TryGetAttribute("open", out _);
                bool expanded = disclosureStates.TryGetValue(id, out bool state) ? state : defaultExpanded;
                _scopes.Add(new Scope(tag.Name, SafeHtmlAlignment.Inherit, SuppressContent: !expanded));
                continue;
            }

            if (tag.Name is "div" or "p" or "center")
            {
                SafeHtmlAlignment alignment = tag.Name == "center"
                    ? SafeHtmlAlignment.Center
                    : SafeHtmlParser.GetAlignment(tag);
                _scopes.Add(new Scope(tag.Name, alignment, SuppressContent: false));
            }
        }

        return isTagSequence &&
            !containsDetailsOpening &&
            tags.All(static tag => tag.Name is "div" or "p" or "center" or "details" or "summary");
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

    private sealed record Scope(string Name, SafeHtmlAlignment Alignment, bool SuppressContent);
}
