using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Extensions;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Hosting;

/// <summary>Immutable request for a viewport-aware hosted element.</summary>
public sealed class MarkdownHostedElementRequest
{
    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    /// <summary>Initializes a hosted-element request.</summary>
    public MarkdownHostedElementRequest(
        string factoryKey,
        SourceSpan sourceRange,
        Rect layoutBounds,
        Rect effectiveViewport,
        IReadOnlyDictionary<string, string>? attributes = null,
        MarkdownAccessibilityRole accessibilityRole = MarkdownAccessibilityRole.Group,
        string? accessibilityName = null,
        string? accessibilityDescription = null,
        string? semanticText = null,
        string? automationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factoryKey);
        FactoryKey = factoryKey.Trim();
        SourceRange = sourceRange;
        LayoutBounds = layoutBounds;
        EffectiveViewport = effectiveViewport;
        Attributes = CopyAttributes(attributes);
        AccessibilityRole = accessibilityRole;
        AccessibilityName = NormalizeOptional(accessibilityName);
        AccessibilityDescription = NormalizeOptional(accessibilityDescription);
        SemanticText = string.IsNullOrWhiteSpace(semanticText) ? null : semanticText;
        AutomationId = NormalizeOptional(automationId);
    }

    /// <summary>Gets the stable factory registration key.</summary>
    public string FactoryKey { get; }
    /// <summary>Gets the half-open UTF-16 source range.</summary>
    public SourceSpan SourceRange { get; }
    /// <summary>Gets the element's document-coordinate layout bounds.</summary>
    public Rect LayoutBounds { get; }
    /// <summary>Gets the current effective viewport in document coordinates.</summary>
    public Rect EffectiveViewport { get; }
    /// <summary>Gets immutable declarative attributes emitted by the extension.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; }
    /// <summary>Gets the requested parser-independent accessibility role.</summary>
    public MarkdownAccessibilityRole AccessibilityRole { get; }
    /// <summary>Gets the requested accessible name.</summary>
    public string? AccessibilityName { get; }
    /// <summary>Gets the requested accessible description/help text.</summary>
    public string? AccessibilityDescription { get; }
    /// <summary>Gets the text contributed to document text and copy operations.</summary>
    public string? SemanticText { get; }
    /// <summary>Gets the renderer-generated stable identity for the logical element.</summary>
    public string? AutomationId { get; }

    private static IReadOnlyDictionary<string, string> CopyAttributes(
        IReadOnlyDictionary<string, string>? attributes)
    {
        if (attributes is null || attributes.Count == 0)
            return EmptyAttributes;

        var copy = new Dictionary<string, string>(attributes.Count, StringComparer.Ordinal);
        foreach (var pair in attributes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            copy.Add(pair.Key, pair.Value ?? string.Empty);
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
