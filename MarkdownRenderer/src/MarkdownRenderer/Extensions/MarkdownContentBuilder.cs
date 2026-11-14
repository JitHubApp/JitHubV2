using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using MarkdownRenderer.Extensions.Internal;
using MarkdownRenderer.Extensions.Vectors;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Extensions;

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
    private MarkdownAsyncNodeRenderer? _deferredSceneRenderer;
    private MarkdownExtensionCallbackLifetime? _deferredSceneRendererLifetime;
    private bool _containsDeferredSceneRenderer;
    private bool _isBuilt;

    internal int EmissionCount => _items.Count;

    internal void CaptureDeferredSceneRenderers(MarkdownAsyncNodeRenderer renderer)
    {
        if (_deferredSceneRenderer is not null)
            return;

        EnsureMutable();
        if (_containsDeferredSceneRenderer)
            _deferredSceneRenderer = renderer;
    }

    internal void BindDeferredSceneRendererLifetime(MarkdownExtensionCallbackLifetime callbackLifetime)
    {
        if (_deferredSceneRenderer is not { } renderer)
            return;

        // The immutable document can be presented by a different control and
        // performance session from the engine that parsed it. Capture the
        // producer's lifetime here so a later consumer cannot lend its own.
        _deferredSceneRendererLifetime = callbackLifetime;
    }

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
            : new MarkdownContentFragment(
                new ReadOnlyCollection<MarkdownContent>(_items.ToArray()),
                _deferredSceneRenderer,
                _deferredSceneRendererLifetime);
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
        IReadOnlyDictionary<string, string> copiedAttributes = CopyAttributes(attributes);

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
            copiedAttributes,
            EmptyChildren,
            accessibilityName,
            accessibilityDescription,
            semanticText));
        if (HasDeferredSceneMarker(copiedAttributes))
            _containsDeferredSceneRenderer = true;
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
            _containsDeferredSceneRenderer |= childBuilder._containsDeferredSceneRenderer;
        }
        IReadOnlyDictionary<string, string> copiedAttributes = CopyAttributes(attributes);

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
            copiedAttributes,
            childItems,
            accessibilityName,
            accessibilityDescription,
            semanticText));
        _containsDeferredSceneRenderer |= HasDeferredSceneMarker(copiedAttributes);
    }

    private static bool CanContainChildren(MarkdownContentKind kind)
        => kind is MarkdownContentKind.Container or
            MarkdownContentKind.Link or
            MarkdownContentKind.List or
            MarkdownContentKind.ListItem or
            MarkdownContentKind.Table or
            MarkdownContentKind.TableRow or
            MarkdownContentKind.TableCell;

    private static bool HasDeferredSceneMarker(IReadOnlyDictionary<string, string> attributes)
        => attributes.TryGetValue(MarkdownContentAttributes.DeferredScene, out string? marker) &&
            !string.IsNullOrWhiteSpace(marker);

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
