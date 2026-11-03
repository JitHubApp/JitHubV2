using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace MarkdownRenderer.Theming;

/// <summary>
/// Partial overrides applied on top of the resolver's defaults. Every property
/// is nullable so callers can selectively override individual fields without
/// disturbing the others — for example, set <see cref="Underline"/> to
/// <c>false</c> to remove the default underline from links, or set
/// <see cref="Margin"/> to <see cref="Thickness"/>.zero to collapse spacing
/// while keeping all other defaults intact.
/// </summary>
public sealed class ElementStyleOverride
{
    /// <summary>Optional font family override.</summary>
    public string? FontFamily { get; init; }
    /// <summary>Optional font size override.</summary>
    public float? FontSize { get; init; }
    /// <summary>Internal relative size declaration used by opt-in inline typography rules.</summary>
    internal float? RelativeFontSizeScale { get; init; }
    /// <summary>Optional font weight override.</summary>
    public Windows.UI.Text.FontWeight? FontWeight { get; init; }
    /// <summary>Optional font style override.</summary>
    public Windows.UI.Text.FontStyle? FontStyle { get; init; }
    /// <summary>Optional foreground color override.</summary>
    public Color? Foreground { get; init; }
    /// <summary>Optional hover foreground color override.</summary>
    public Color? HoverForeground { get; init; }
    /// <summary>Optional focus foreground color override.</summary>
    public Color? FocusForeground { get; init; }
    /// <summary>Optional background color override.</summary>
    public Color? Background { get; init; }
    /// <summary>Optional accent bar color override.</summary>
    public Color? AccentBar { get; init; }
    /// <summary>Optional accent bar thickness override in device-independent pixels.</summary>
    public float? AccentBarThickness { get; init; }
    /// <summary>Optional override for whether the accent bar consumes a content inset.</summary>
    public bool? AccentBarAffectsContentLayout { get; init; }
    /// <summary>Optional border color override.</summary>
    public Color? BorderBrush { get; init; }
    /// <summary>
    /// Gets whether an h1/h2 border is painted only along its bottom edge.
    /// Set this when customizing a profile heading rule's brush or thickness
    /// while retaining its bottom-rule geometry.
    /// </summary>
    public bool? HeadingBorderBottomOnly { get; init; }
    /// <summary>Optional border thickness override.</summary>
    public float? BorderThickness { get; init; }
    /// <summary>Optional corner radius override.</summary>
    public float? CornerRadius { get; init; }
    /// <summary>Optional base list indentation override.</summary>
    public float? ListIndent { get; init; }
    /// <summary>Optional nested list indentation override.</summary>
    public float? NestedListIndent { get; init; }
    /// <summary>Optional underline override.</summary>
    public bool? Underline { get; init; }
    /// <summary>Optional strikethrough override.</summary>
    public bool? Strikethrough { get; init; }
    /// <summary>Optional margin override.</summary>
    public Thickness? Margin { get; init; }
    /// <summary>Optional padding override.</summary>
    public Thickness? Padding { get; init; }
    /// <summary>Optional line-height multiplier override.</summary>
    public float? LineHeightMultiplier { get; init; }
}
