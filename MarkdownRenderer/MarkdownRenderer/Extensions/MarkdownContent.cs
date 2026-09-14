using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Extensions;

/// <summary>Identifies a parser-independent declarative content primitive.</summary>
public enum MarkdownContentKind
{
    /// <summary>Semantic text.</summary>
    Text,
    /// <summary>A generic semantic container.</summary>
    Container,
    /// <summary>A hyperlink container.</summary>
    Link,
    /// <summary>A list container.</summary>
    List,
    /// <summary>A list item container.</summary>
    ListItem,
    /// <summary>A table container.</summary>
    Table,
    /// <summary>A table row container.</summary>
    TableRow,
    /// <summary>A table cell container.</summary>
    TableCell,
    /// <summary>A code block.</summary>
    CodeBlock,
    /// <summary>An image resolved through the host's image policy.</summary>
    Image,
    /// <summary>An immutable painter-neutral vector scene.</summary>
    VectorScene,
    /// <summary>A hosted element resolved through a registered factory key.</summary>
    HostedElement,
    /// <summary>A named extension primitive interpreted by a feature pack.</summary>
    Custom
}

/// <summary>Stable layout attributes understood for hosted elements.</summary>
public static class MarkdownHostedElementAttributes
{
    /// <summary>
    /// Optional invariant-culture device-independent height reserved during
    /// background layout. Invalid values use the active style's default height.
    /// </summary>
    public const string DesiredHeight = "layout.height";
}

/// <summary>
/// Stable declarative attributes understood by the native WinUI adapters.
/// Values use invariant culture and unknown attributes are ignored.
/// </summary>
public static class MarkdownContentAttributes
{
    /// <summary><c>true</c> when a list is ordered.</summary>
    public const string ListOrdered = "list.ordered";

    /// <summary>One-based starting ordinal for an ordered list.</summary>
    public const string ListStart = "list.start";

    /// <summary><c>true</c> when a table row contains column headers.</summary>
    public const string TableHeaderRow = "table.header";

    /// <summary>Optional image width in DIPs or as a percentage such as <c>50%</c>.</summary>
    public const string ImageWidth = "image.width";

    /// <summary>Optional image height in DIPs or as a percentage such as <c>50%</c>.</summary>
    public const string ImageHeight = "image.height";

    /// <summary>Optional title/help text for links and images.</summary>
    public const string Title = "content.title";

    /// <summary>Optional <c>true</c>/<c>false</c> line-number override for code blocks.</summary>
    public const string CodeShowLineNumbers = "code.lineNumbers";

    /// <summary>Optional one-based first line number for code blocks.</summary>
    public const string CodeStartLine = "code.startLine";
}

/// <summary>Accessibility semantics carried by declarative extension content.</summary>
public enum MarkdownAccessibilityRole
{
    /// <summary>No additional semantic role.</summary>
    None,
    /// <summary>Text content.</summary>
    Text,
    /// <summary>A semantic group.</summary>
    Group,
    /// <summary>A paragraph.</summary>
    Paragraph,
    /// <summary>A heading.</summary>
    Heading,
    /// <summary>An invokable hyperlink.</summary>
    Link,
    /// <summary>An image.</summary>
    Image,
    /// <summary>A code region.</summary>
    Code,
    /// <summary>A list.</summary>
    List,
    /// <summary>A list item.</summary>
    ListItem,
    /// <summary>A table.</summary>
    Table,
    /// <summary>A table row.</summary>
    Row,
    /// <summary>A table cell.</summary>
    Cell,
    /// <summary>A mathematical expression.</summary>
    Math,
    /// <summary>A diagram.</summary>
    Diagram,
    /// <summary>A live status message.</summary>
    Status
}

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

/// <summary>An immutable content fragment produced by one extension renderer.</summary>
public sealed class MarkdownContentFragment
{
    private static readonly IReadOnlyList<MarkdownContent> EmptyItems = Array.Empty<MarkdownContent>();

    internal MarkdownContentFragment(IReadOnlyList<MarkdownContent> items)
    {
        Items = items;
    }

    /// <summary>Gets an empty fragment.</summary>
    public static MarkdownContentFragment Empty { get; } = new(EmptyItems);

    /// <summary>Gets the fragment's top-level content in semantic order.</summary>
    public IReadOnlyList<MarkdownContent> Items { get; }
}

/// <summary>
/// Collects parser-independent content primitives and freezes them into an
/// immutable <see cref="MarkdownContentFragment"/>.
/// </summary>
public sealed class MarkdownContentBuilder
{
    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    private static readonly IReadOnlyList<MarkdownContent> EmptyChildren = Array.Empty<MarkdownContent>();

    private readonly List<MarkdownContent> _items = new();
    private bool _isBuilt;

    internal int EmissionCount => _items.Count;

    /// <summary>Adds semantic text.</summary>
    public MarkdownContentBuilder AddText(
        string text,
        SourceSpan sourceSpan,
        MarkdownStyleRole styleRole = default,
        MarkdownAccessibilityRole accessibilityRole = MarkdownAccessibilityRole.Text)
    {
        ArgumentNullException.ThrowIfNull(text);
        AddLeaf(
            MarkdownContentKind.Text,
            NormalizeRole(styleRole, MarkdownStyleRole.Body),
            sourceSpan,
            accessibilityRole,
            text: text);
        return this;
    }

    /// <summary>Adds a code block.</summary>
    public MarkdownContentBuilder AddCodeBlock(
        string code,
        string? language,
        SourceSpan sourceSpan,
        MarkdownStyleRole styleRole = default,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        AddLeaf(
            MarkdownContentKind.CodeBlock,
            NormalizeRole(styleRole, MarkdownStyleRole.CodeBlock),
            sourceSpan,
            MarkdownAccessibilityRole.Code,
            text: code,
            language: NormalizeOptional(language),
            attributes: attributes);
        return this;
    }

    /// <summary>Adds an image resolved by the host's image policy.</summary>
    public MarkdownContentBuilder AddImage(
        string destination,
        string? alternativeText,
        SourceSpan sourceSpan,
        MarkdownStyleRole styleRole = default,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        AddLeaf(
            MarkdownContentKind.Image,
            NormalizeRole(styleRole, MarkdownStyleRole.ImageCaption),
            sourceSpan,
            MarkdownAccessibilityRole.Image,
            text: alternativeText,
            destination: destination,
            attributes: attributes);
        return this;
    }

    /// <summary>
    /// Adds an atomic painter-neutral vector scene. The semantic text is used
    /// for rendered-text copy; the accessibility description is exposed as
    /// automation help text and can preserve the original source.
    /// </summary>
    public MarkdownContentBuilder AddVectorScene(
        MarkdownVectorScene scene,
        SourceSpan sourceSpan,
        MarkdownStyleRole styleRole,
        MarkdownAccessibilityRole accessibilityRole,
        string? accessibilityName = null,
        string? accessibilityDescription = null,
        string? semanticText = null,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (styleRole.IsEmpty)
            throw new ArgumentException("A vector scene style role is required.", nameof(styleRole));

        AddLeaf(
            MarkdownContentKind.VectorScene,
            styleRole,
            sourceSpan,
            accessibilityRole,
            vectorScene: scene,
            attributes: attributes,
            accessibilityName: NormalizeOptional(accessibilityName),
            accessibilityDescription: NormalizeOptional(accessibilityDescription),
            semanticText: NormalizeSemanticText(semanticText));
        return this;
    }

    /// <summary>Adds a hosted element by factory key without exposing a UI type.</summary>
    public MarkdownContentBuilder AddHostedElement(
        string factoryKey,
        SourceSpan sourceSpan,
        MarkdownStyleRole styleRole,
        MarkdownAccessibilityRole accessibilityRole = MarkdownAccessibilityRole.Group,
        IReadOnlyDictionary<string, string>? attributes = null,
        string? accessibilityName = null,
        string? accessibilityDescription = null,
        string? semanticText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factoryKey);
        if (styleRole.IsEmpty)
            throw new ArgumentException("A hosted element style role is required.", nameof(styleRole));

        AddLeaf(
            MarkdownContentKind.HostedElement,
            styleRole,
            sourceSpan,
            accessibilityRole,
            factoryKey: factoryKey.Trim(),
            attributes: attributes,
            accessibilityName: NormalizeOptional(accessibilityName),
            accessibilityDescription: NormalizeOptional(accessibilityDescription),
            semanticText: NormalizeSemanticText(semanticText));
        return this;
    }

    /// <summary>Adds a named feature-pack primitive.</summary>
    public MarkdownContentBuilder AddCustom(
        string customKind,
        SourceSpan sourceSpan,
        MarkdownStyleRole styleRole,
        MarkdownAccessibilityRole accessibilityRole,
        IReadOnlyDictionary<string, string>? attributes = null,
        Action<MarkdownContentBuilder>? children = null,
        string? accessibilityName = null,
        string? accessibilityDescription = null,
        string? semanticText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customKind);
        if (styleRole.IsEmpty)
            throw new ArgumentException("A custom primitive style role is required.", nameof(styleRole));

        AddContainerCore(
            MarkdownContentKind.Custom,
            styleRole,
            sourceSpan,
            accessibilityRole,
            children,
            customKind: customKind.Trim(),
            attributes: attributes,
            accessibilityName: NormalizeOptional(accessibilityName),
            accessibilityDescription: NormalizeOptional(accessibilityDescription),
            semanticText: NormalizeSemanticText(semanticText));
        return this;
    }

    /// <summary>Adds a semantic container, table, list, cell, row, or link.</summary>
    public MarkdownContentBuilder AddContainer(
        MarkdownContentKind kind,
        MarkdownStyleRole styleRole,
        SourceSpan sourceSpan,
        MarkdownAccessibilityRole accessibilityRole,
        Action<MarkdownContentBuilder> children,
        string? destination = null,
        IReadOnlyDictionary<string, string>? attributes = null,
        string? accessibilityName = null,
        string? accessibilityDescription = null,
        string? semanticText = null)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (!CanContainChildren(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), "The selected content kind cannot contain children.");
        if (styleRole.IsEmpty)
            throw new ArgumentException("A container style role is required.", nameof(styleRole));
        if (kind == MarkdownContentKind.Link && string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("Links require a destination.", nameof(destination));

        AddContainerCore(
            kind,
            styleRole,
            sourceSpan,
            accessibilityRole,
            children,
            destination: destination,
            attributes: attributes,
            accessibilityName: NormalizeOptional(accessibilityName),
            accessibilityDescription: NormalizeOptional(accessibilityDescription),
            semanticText: NormalizeSemanticText(semanticText));
        return this;
    }

    /// <summary>Freezes the collected content.</summary>
    public MarkdownContentFragment Build()
    {
        EnsureMutable();
        _isBuilt = true;

        return _items.Count == 0
            ? MarkdownContentFragment.Empty
            : new MarkdownContentFragment(new ReadOnlyCollection<MarkdownContent>(_items.ToArray()));
    }

    private void AddLeaf(
        MarkdownContentKind kind,
        MarkdownStyleRole styleRole,
        SourceSpan sourceSpan,
        MarkdownAccessibilityRole accessibilityRole,
        string? text = null,
        string? destination = null,
        string? language = null,
        string? factoryKey = null,
        MarkdownVectorScene? vectorScene = null,
        IReadOnlyDictionary<string, string>? attributes = null,
        string? accessibilityName = null,
        string? accessibilityDescription = null,
        string? semanticText = null)
    {
        EnsureMutable();
        MarkdownSyntaxNode.ValidateSourceSpan(sourceSpan);

        _items.Add(new MarkdownContent(
            kind,
            styleRole,
            sourceSpan,
            accessibilityRole,
            text,
            destination,
            language,
            factoryKey,
            customKind: null,
            vectorScene,
            CopyAttributes(attributes),
            EmptyChildren,
            accessibilityName,
            accessibilityDescription,
            semanticText));
    }

    private void AddContainerCore(
        MarkdownContentKind kind,
        MarkdownStyleRole styleRole,
        SourceSpan sourceSpan,
        MarkdownAccessibilityRole accessibilityRole,
        Action<MarkdownContentBuilder>? children,
        string? destination = null,
        string? customKind = null,
        IReadOnlyDictionary<string, string>? attributes = null,
        string? accessibilityName = null,
        string? accessibilityDescription = null,
        string? semanticText = null)
    {
        EnsureMutable();
        MarkdownSyntaxNode.ValidateSourceSpan(sourceSpan);

        IReadOnlyList<MarkdownContent> childItems = EmptyChildren;
        if (children is not null)
        {
            var childBuilder = new MarkdownContentBuilder();
            children(childBuilder);
            childItems = childBuilder.Build().Items;
        }

        _items.Add(new MarkdownContent(
            kind,
            styleRole,
            sourceSpan,
            accessibilityRole,
            text: null,
            destination,
            language: null,
            factoryKey: null,
            customKind,
            vectorScene: null,
            CopyAttributes(attributes),
            childItems,
            accessibilityName,
            accessibilityDescription,
            semanticText));
    }

    private static bool CanContainChildren(MarkdownContentKind kind)
        => kind is MarkdownContentKind.Container or
            MarkdownContentKind.Link or
            MarkdownContentKind.List or
            MarkdownContentKind.ListItem or
            MarkdownContentKind.Table or
            MarkdownContentKind.TableRow or
            MarkdownContentKind.TableCell;

    private static MarkdownStyleRole NormalizeRole(
        MarkdownStyleRole role,
        MarkdownStyleRole fallback)
        => role.IsEmpty ? fallback : role;

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeSemanticText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static IReadOnlyDictionary<string, string> CopyAttributes(
        IReadOnlyDictionary<string, string>? attributes)
    {
        if (attributes is null || attributes.Count == 0)
            return EmptyAttributes;

        var copy = new Dictionary<string, string>(attributes.Count, StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(attribute.Key);
            copy.Add(attribute.Key, attribute.Value ?? string.Empty);
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    private void EnsureMutable()
    {
        if (_isBuilt)
            throw new InvalidOperationException("The content builder has already been built.");
    }
}
