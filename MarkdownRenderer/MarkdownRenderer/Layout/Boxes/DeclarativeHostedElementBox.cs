using System;
using System.Collections.Generic;
using System.Globalization;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Device-independent layout reservation for declarative hosted content. It
/// contains no XAML element or factory reference, so construction and measure
/// remain safe on a layout worker.
/// </summary>
internal sealed class DeclarativeHostedElementBox : BlockBox
{
    private const float DefaultMinimumHeight = 32f;
    private const float MaximumHeight = 16_384f;
    private readonly MarkdownLayoutContext _context;
    private readonly float _desiredHeight;

    internal DeclarativeHostedElementBox(
        MarkdownLayoutContext context,
        MarkdownContent content,
        HostedElementFallbackKey fallbackKey)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        ArgumentNullException.ThrowIfNull(content);
        if (content.Kind != MarkdownContentKind.HostedElement ||
            string.IsNullOrWhiteSpace(content.FactoryKey))
        {
            throw new ArgumentException("Hosted content and a factory key are required.", nameof(content));
        }

        FactoryKey = content.FactoryKey;
        SourceRange = content.SourceSpan;
        Attributes = content.Attributes;
        AccessibilityRole = content.AccessibilityRole;
        AccessibilityName = content.AccessibilityName;
        AccessibilityDescription = content.AccessibilityDescription;
        SemanticText = content.SemanticText;
        FallbackKey = fallbackKey;

        var style = context.ThemeSnapshot.GetStyle(
            content.StyleRole.Name,
            context.CreateStyleContextSnapshot(),
            context.CreateStyleAliasSnapshot());
        Margin = style.Margin;
        float defaultHeight = Math.Max(
            DefaultMinimumHeight,
            style.FontSize * Math.Max(1f, style.LineHeightMultiplier) +
            (float)(style.Padding.Top + style.Padding.Bottom));
        _desiredHeight = ResolveDesiredHeight(content.Attributes, defaultHeight);
    }

    internal string FactoryKey { get; }

    internal SourceSpan SourceRange { get; }

    internal IReadOnlyDictionary<string, string> Attributes { get; }

    internal MarkdownAccessibilityRole AccessibilityRole { get; }

    internal string? AccessibilityName { get; }

    internal string? AccessibilityDescription { get; }

    internal string? SemanticText { get; }

    internal string AutomationId =>
        HostedElementAutomationIdentity.Create(FactoryKey, SourceRange, BlockIndex);

    internal HostedElementFallbackKey FallbackKey { get; }

    internal Microsoft.UI.Xaml.FrameworkElement? RealizedElement { get; set; }

    public override float Measure(float availableWidth)
    {
        ThrowIfCancellationRequested();
        float total = _desiredHeight + (float)(Margin.Top + Margin.Bottom);
        Bounds = new Rect(0, 0, Math.Max(1, availableWidth), total);
        return total;
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        // The XAML overlay owns painting for a realized hosted element.
    }

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        position = new DocumentPosition(
            BlockIndex,
            0,
            point.Y >= Bounds.Y + Bounds.Height / 2.0 ? 1 : 0);
        return Bounds.Contains(point);
    }

    internal override void ThrowIfCancellationRequested()
        => _context.CancellationToken.ThrowIfCancellationRequested();

    private static float ResolveDesiredHeight(
        IReadOnlyDictionary<string, string> attributes,
        float fallback)
    {
        if (!attributes.TryGetValue(MarkdownHostedElementAttributes.DesiredHeight, out string? text) ||
            !float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ||
            !float.IsFinite(value) ||
            value <= 0)
        {
            return fallback;
        }

        return Math.Clamp(value, 1f, MaximumHeight);
    }
}
