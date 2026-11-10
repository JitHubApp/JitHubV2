using System;
using System.Collections.Generic;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Internal.Building;

/// <summary>
/// Style context keys, style aliases and list depth that one layout pass pushes
/// while it descends the document. Boxes snapshot the current keys so the theme
/// resolves their styles; each push returns a scope that restores the stack.
/// </summary>
internal sealed class LayoutStyleStack
{
    private readonly List<string> _contextKeys = new();
    private readonly List<string> _aliasKeys = new();
    private int _listDepth;

    /// <summary>Nesting depth of the lists that enclose the current construction point.</summary>
    internal int ListDepth => _listDepth;

    /// <summary>Number of style aliases currently pushed; a later snapshot can start from it.</summary>
    internal int AliasCount => _aliasKeys.Count;

    internal IReadOnlyList<string> CreateContextSnapshot()
        => _contextKeys.Count == 0 ? Array.Empty<string>() : _contextKeys.ToArray();

    internal IReadOnlyList<string> CreateAliasSnapshot()
        => _aliasKeys.Count == 0 ? Array.Empty<string>() : _aliasKeys.ToArray();

    internal IReadOnlyList<string> CreateAliasSnapshotFrom(int startIndex)
    {
        startIndex = Math.Clamp(startIndex, 0, _aliasKeys.Count);
        int count = _aliasKeys.Count - startIndex;
        if (count == 0)
            return Array.Empty<string>();

        var result = new string[count];
        _aliasKeys.CopyTo(startIndex, result, 0, count);
        return result;
    }

    internal LayoutStyleScope PushContext(string key)
    {
        LayoutStyleScope scope = CaptureScope();
        if (!string.IsNullOrWhiteSpace(key))
            _contextKeys.Add(key);
        return scope;
    }

    internal LayoutStyleScope PushListDepth()
    {
        LayoutStyleScope scope = CaptureScope();
        _listDepth++;
        _contextKeys.Add(MarkdownElementKeys.ListDepth(_listDepth));
        return scope;
    }

    internal LayoutStyleScope PushMarkdownAttributes(IMarkdownObject markdownObject)
    {
        LayoutStyleScope scope = CaptureScope();
        AddMarkdownAttributeAliases(markdownObject, _aliasKeys);
        return scope;
    }

    internal LayoutStyleScope PushAliases(IReadOnlyList<string> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        LayoutStyleScope scope = CaptureScope();
        for (int index = 0; index < aliases.Count; index++)
        {
            string alias = aliases[index];
            if (!string.IsNullOrWhiteSpace(alias))
                _aliasKeys.Add(alias);
        }

        return scope;
    }

    internal void Restore(int contextCount, int aliasCount, int listDepth)
    {
        if (_contextKeys.Count > contextCount)
            _contextKeys.RemoveRange(contextCount, _contextKeys.Count - contextCount);
        if (_aliasKeys.Count > aliasCount)
            _aliasKeys.RemoveRange(aliasCount, _aliasKeys.Count - aliasCount);
        _listDepth = listDepth;
    }

    private LayoutStyleScope CaptureScope() =>
        new(this, _contextKeys.Count, _aliasKeys.Count, _listDepth);

    private static void AddMarkdownAttributeAliases(IMarkdownObject markdownObject, List<string> aliases)
    {
        var attrs = HtmlAttributesExtensions.TryGetAttributes(markdownObject);
        if (attrs is null)
            return;

        if (attrs.Classes is not null)
        {
            foreach (var @class in attrs.Classes)
            {
                if (!string.IsNullOrWhiteSpace(@class))
                    aliases.Add(MarkdownElementKeys.Class(@class));
            }
        }

        if (!string.IsNullOrWhiteSpace(attrs.Id))
            aliases.Add(MarkdownElementKeys.Id(attrs.Id));
    }
}
