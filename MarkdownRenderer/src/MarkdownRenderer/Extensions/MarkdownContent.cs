using System.Collections.Generic;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Extensions;

/// <summary>An immutable declarative content node emitted by an extension.</summary>
public sealed class MarkdownContent
{
    internal MarkdownContent(
        MarkdownContentKind kind,
        MarkdownStyleRole styleRole,
        SourceSpan sourceSpan,
        MarkdownAccessibilityRole accessibilityRole,
        string? text,
        string? destination,
        string? language,
        string? factoryKey,
        string? customKind,
        MarkdownVectorScene? vectorScene,
        IReadOnlyDictionary<string, string> attributes,
        IReadOnlyList<MarkdownContent> children,
        string? accessibilityName,
        string? accessibilityDescription,
        string? semanticText)
    {
        Kind = kind;
        StyleRole = styleRole;
        SourceSpan = sourceSpan;
        AccessibilityRole = accessibilityRole;
        Text = text;
        Destination = destination;
        Language = language;
        FactoryKey = factoryKey;
        CustomKind = customKind;
        VectorScene = vectorScene;
        Attributes = attributes;
        Children = children;
        AccessibilityName = accessibilityName;
        AccessibilityDescription = accessibilityDescription;
        SemanticText = semanticText;
    }

    /// <summary>Gets the declarative primitive kind.</summary>
    public MarkdownContentKind Kind { get; }
    /// <summary>Gets the semantic style role.</summary>
    public MarkdownStyleRole StyleRole { get; }
    /// <summary>Gets the half-open UTF-16 source range.</summary>
    public SourceSpan SourceSpan { get; }
    /// <summary>Gets the accessibility role.</summary>
    public MarkdownAccessibilityRole AccessibilityRole { get; }
    /// <summary>Gets text or alternative text carried by this node.</summary>
    public string? Text { get; }
    /// <summary>Gets a link or image destination.</summary>
    public string? Destination { get; }
    /// <summary>Gets a code or document language.</summary>
    public string? Language { get; }
    /// <summary>Gets the host factory key for a hosted element.</summary>
    public string? FactoryKey { get; }
    /// <summary>Gets the pack-qualified primitive name for custom content.</summary>
    public string? CustomKind { get; }
    /// <summary>Gets the immutable vector scene for vector content.</summary>
    public MarkdownVectorScene? VectorScene { get; }
    /// <summary>Gets immutable, serializable metadata.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; }
    /// <summary>Gets child nodes in semantic order.</summary>
    public IReadOnlyList<MarkdownContent> Children { get; }
    /// <summary>Gets the requested accessible name for an atomic content node.</summary>
    public string? AccessibilityName { get; }
    /// <summary>Gets the requested accessible description/help text.</summary>
    public string? AccessibilityDescription { get; }
    /// <summary>Gets the text contributed to document text and copy operations.</summary>
    public string? SemanticText { get; }

}
