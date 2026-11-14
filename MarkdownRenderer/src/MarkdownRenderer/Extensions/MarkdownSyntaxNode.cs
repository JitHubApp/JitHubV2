using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace MarkdownRenderer.Extensions;

/// <summary>
/// A parser-independent, immutable syntax node presented to an extension renderer.
/// </summary>
public sealed class MarkdownSyntaxNode
{
    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    private static readonly IReadOnlyList<MarkdownSyntaxNode> EmptyChildren =
        Array.Empty<MarkdownSyntaxNode>();

    /// <summary>Initializes an immutable syntax node.</summary>
    public MarkdownSyntaxNode(
        string kind,
        SourceSpan sourceSpan,
        string? literal = null,
        IEnumerable<KeyValuePair<string, string>>? attributes = null,
        IEnumerable<MarkdownSyntaxNode>? children = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ValidateSourceSpan(sourceSpan);

        Kind = kind.Trim();
        SourceSpan = sourceSpan;
        Literal = literal;
        Attributes = CopyAttributes(attributes);
        Children = CopyChildren(children);
    }

    /// <summary>Gets the stable syntax kind used for exact renderer dispatch.</summary>
    public string Kind { get; }
    /// <summary>Gets the half-open UTF-16 source range.</summary>
    public SourceSpan SourceSpan { get; }
    /// <summary>Gets the node's literal value, when one exists.</summary>
    public string? Literal { get; }
    /// <summary>Gets parser-normalized string attributes.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; }
    /// <summary>Gets immutable child nodes in source order.</summary>
    public IReadOnlyList<MarkdownSyntaxNode> Children { get; }

    private static IReadOnlyDictionary<string, string> CopyAttributes(
        IEnumerable<KeyValuePair<string, string>>? attributes)
    {
        if (attributes is null)
            return EmptyAttributes;

        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(attribute.Key);
            copy.Add(attribute.Key, attribute.Value ?? string.Empty);
        }

        return copy.Count == 0
            ? EmptyAttributes
            : new ReadOnlyDictionary<string, string>(copy);
    }

    private static IReadOnlyList<MarkdownSyntaxNode> CopyChildren(
        IEnumerable<MarkdownSyntaxNode>? children)
    {
        if (children is null)
            return EmptyChildren;

        var copy = new List<MarkdownSyntaxNode>();
        foreach (var child in children)
            copy.Add(child ?? throw new ArgumentException("Syntax nodes cannot contain null children.", nameof(children)));

        return copy.Count == 0
            ? EmptyChildren
            : new ReadOnlyCollection<MarkdownSyntaxNode>(copy.ToArray());
    }

    internal static void ValidateSourceSpan(SourceSpan sourceSpan)
    {
        if (sourceSpan.Start < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceSpan), "Source offsets cannot be negative.");
        if (sourceSpan.Length < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceSpan), "Source lengths cannot be negative.");
    }
}
