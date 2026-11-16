using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Layout.Internal.Inlines.Runs;

/// <summary>
/// A persistent, source-ordered style path. Nodes are shared by adjacent text
/// ranges and immutable after the parser publishes the containing map.
/// </summary>
internal sealed class LinkInlineStylePath
{
    private readonly IReadOnlyList<string> _styleAliases;

    internal LinkInlineStylePath(
        string styleKey,
        IReadOnlyList<string> styleAliases,
        LinkInlineStylePath? parent)
    {
        StyleKey = string.IsNullOrWhiteSpace(styleKey)
            ? throw new ArgumentException("A nested inline style key is required.", nameof(styleKey))
            : styleKey;
        ArgumentNullException.ThrowIfNull(styleAliases);
        if (styleAliases.Count == 0)
        {
            _styleAliases = Array.Empty<string>();
        }
        else
        {
            var copy = new string[styleAliases.Count];
            for (int index = 0; index < copy.Length; index++)
                copy[index] = styleAliases[index];
            _styleAliases = Array.AsReadOnly(copy);
        }
        Parent = parent;
        Depth = parent is null ? 1 : checked(parent.Depth + 1);
    }

    internal string StyleKey { get; }
    internal IReadOnlyList<string> StyleAliases => _styleAliases;
    internal LinkInlineStylePath? Parent { get; }
    internal int Depth { get; }
}
