using System;

namespace MarkdownRenderer.Theming;

/// <summary>
/// Identifies the semantic role targeted by a markdown style rule.
/// </summary>
/// <remarks>
/// Roles are value-like, case-sensitive identifiers. Names used by global
/// renderer resources (Document, Selection, FocusVisual, Interaction, and
/// Overflow) are reserved. Built-in roles map to the
/// existing markdown element-key values so a style sheet can be layered over
/// a resolved WinUI theme without changing the
/// legacy theme API. Extensions may introduce their own stable role names.
/// </remarks>
public readonly struct MarkdownStyleRole : IEquatable<MarkdownStyleRole>
{
    private static readonly string[] ReservedGlobalResourceScopes =
    [
        "Document",
        "Selection",
        "FocusVisual",
        "Interaction",
        "Overflow",
    ];

    private readonly string? _name;

    /// <summary>Initializes a custom style role.</summary>
    /// <param name="name">
    /// A non-empty stable role identifier. Leading and trailing whitespace is
    /// removed before the identifier is validated.
    /// </param>
    public MarkdownStyleRole(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        name = name.Trim();
        if (name.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(name), "Style role names cannot exceed 128 characters.");
        if (IsReservedGlobalResourceScope(name))
        {
            throw new ArgumentException(
                $"'{name}' is reserved for global MarkdownRenderer resources.",
                nameof(name));
        }

        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsControl(name[i]))
                throw new ArgumentException("Style role names cannot contain control characters.", nameof(name));
        }

        _name = name;
    }

    /// <summary>Gets the stable role identifier.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>Gets whether this is an uninitialized role value.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_name);

    /// <summary>Body paragraph text.</summary>
    public static MarkdownStyleRole Body { get; } = new("Body");
    /// <summary>Level-one heading text.</summary>
    public static MarkdownStyleRole Heading1 { get; } = new("Heading1");
    /// <summary>Level-two heading text.</summary>
    public static MarkdownStyleRole Heading2 { get; } = new("Heading2");
    /// <summary>Level-three heading text.</summary>
    public static MarkdownStyleRole Heading3 { get; } = new("Heading3");
    /// <summary>Level-four heading text.</summary>
    public static MarkdownStyleRole Heading4 { get; } = new("Heading4");
    /// <summary>Level-five heading text.</summary>
    public static MarkdownStyleRole Heading5 { get; } = new("Heading5");
    /// <summary>Level-six heading text.</summary>
    public static MarkdownStyleRole Heading6 { get; } = new("Heading6");
    /// <summary>Inline code text.</summary>
    public static MarkdownStyleRole InlineCode { get; } = new("CodeInline");
    /// <summary>Fenced or indented code block.</summary>
    public static MarkdownStyleRole CodeBlock { get; } = new("CodeBlock");
    /// <summary>Code block header.</summary>
    public static MarkdownStyleRole CodeBlockHeader { get; } = new("CodeBlockHeader");
    /// <summary>Code block language label.</summary>
    public static MarkdownStyleRole CodeBlockLanguage { get; } = new("CodeBlockLanguage");
    /// <summary>Code block line-number gutter.</summary>
    public static MarkdownStyleRole CodeBlockGutter { get; } = new("CodeBlockGutter");
    /// <summary>Code block line-number text.</summary>
    public static MarkdownStyleRole CodeBlockLineNumber { get; } = new("CodeBlockLineNumber");
    /// <summary>Block quote container.</summary>
    public static MarkdownStyleRole Quote { get; } = new("Quote");
    /// <summary>Inline link text.</summary>
    public static MarkdownStyleRole Link { get; } = new("Link");
    /// <summary>Strong emphasis text.</summary>
    public static MarkdownStyleRole Strong { get; } = new("Strong");
    /// <summary>Emphasis text.</summary>
    public static MarkdownStyleRole Emphasis { get; } = new("Emphasis");
    /// <summary>Struck-through text.</summary>
    public static MarkdownStyleRole Strikethrough { get; } = new("Strikethrough");
    /// <summary>List marker text.</summary>
    public static MarkdownStyleRole ListMarker { get; } = new("ListMarker");
    /// <summary>Thematic break separator.</summary>
    public static MarkdownStyleRole ThematicBreak { get; } = new("ThematicBreak");
    /// <summary>Image caption or alternative text.</summary>
    public static MarkdownStyleRole ImageCaption { get; } = new("ImageCaption");
    /// <summary>Table container.</summary>
    public static MarkdownStyleRole Table { get; } = new("Table");
    /// <summary>Table header cell.</summary>
    public static MarkdownStyleRole TableHeader { get; } = new("TableHeader");
    /// <summary>Table body cell.</summary>
    public static MarkdownStyleRole TableCell { get; } = new("TableCell");
    /// <summary>Diagram surface.</summary>
    public static MarkdownStyleRole Diagram { get; } = new("Diagram");
    /// <summary>Inline or display mathematical expression.</summary>
    public static MarkdownStyleRole Math { get; } = new("Math");

    /// <summary>Creates a role from a legacy markdown element key.</summary>
    public static MarkdownStyleRole FromElementKey(string elementKey) => new(elementKey);

    /// <summary>
    /// Tries to project an exact legacy element key into the stricter resource
    /// role namespace without normalizing or throwing for arbitrary aliases.
    /// </summary>
    internal static bool TryCreateCanonical(string? name, out MarkdownStyleRole role)
    {
        role = default;
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length > 128 ||
            !string.Equals(name, name.Trim(), StringComparison.Ordinal) ||
            IsReservedGlobalResourceScope(name))
        {
            return false;
        }

        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsControl(name[i]))
                return false;
        }

        role = new MarkdownStyleRole(name);
        return true;
    }

    internal static bool IsReservedGlobalResourceScope(string value)
        => Array.IndexOf(ReservedGlobalResourceScopes, value) >= 0;

    /// <inheritdoc />
    public bool Equals(MarkdownStyleRole other)
        => string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is MarkdownStyleRole other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => StringComparer.Ordinal.GetHashCode(Name);

    /// <inheritdoc />
    public override string ToString() => Name;

    /// <summary>Compares two roles by their stable identifiers.</summary>
    public static bool operator ==(MarkdownStyleRole left, MarkdownStyleRole right) => left.Equals(right);
    /// <summary>Compares two roles by their stable identifiers.</summary>
    public static bool operator !=(MarkdownStyleRole left, MarkdownStyleRole right) => !left.Equals(right);
}
