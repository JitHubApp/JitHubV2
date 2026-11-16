using Microsoft.UI.Xaml;
using Windows.UI;

namespace MarkdownRenderer.Theming;

/// <summary>
/// Fully resolved style used by layout and painting.
/// </summary>
public sealed class ElementStyle
{
    /// <summary>
    /// DirectWrite font fallback chain. Include Segoe UI Emoji for emoji support.
    /// </summary>
    public string FontFamily { get; init; } = "Segoe UI Variable, Segoe UI Emoji, Segoe UI Symbol";
    /// <summary>Font size in device-independent pixels.</summary>
    public float FontSize { get; init; } = 14f;
    /// <summary>Internal percentage declaration used by relative inline typography rules.</summary>
    internal float? RelativeFontSizeScale { get; init; }
    /// <summary>Font weight.</summary>
    public Windows.UI.Text.FontWeight FontWeight { get; init; } = new() { Weight = 400 };
    /// <summary>Font style.</summary>
    public Windows.UI.Text.FontStyle FontStyle { get; init; } = Windows.UI.Text.FontStyle.Normal;
    /// <summary>Primary foreground color.</summary>
    public Color Foreground { get; init; } = Color.FromArgb(0xFF, 0, 0, 0);
    /// <summary>Optional foreground color for hovered links.</summary>
    public Color? HoverForeground { get; init; }
    /// <summary>Optional foreground color for keyboard-focused links.</summary>
    public Color? FocusForeground { get; init; }
    /// <summary>Optional background fill color.</summary>
    public Color? Background { get; init; }
    /// <summary>Optional accent bar color for quote-like containers.</summary>
    public Color? AccentBar { get; init; }
    /// <summary>Thickness of an accent bar in device-independent pixels.</summary>
    public float AccentBarThickness { get; init; } = 3f;
    /// <summary>
    /// Gets whether an accent bar consumes its own leading content inset instead
    /// of being painted over the element's padding.
    /// </summary>
    public bool AccentBarAffectsContentLayout { get; init; }
    /// <summary>Optional border color.</summary>
    public Color? BorderBrush { get; init; }
    /// <summary>Internal profile hint for rendering a heading border as a bottom rule.</summary>
    internal bool HeadingBorderBottomOnly { get; init; }
    /// <summary>Border thickness in device-independent pixels.</summary>
    public float BorderThickness { get; init; }
    /// <summary>Corner radius in device-independent pixels.</summary>
    public float CornerRadius { get; init; }
    /// <summary>Base list indentation in device-independent pixels.</summary>
    public float ListIndent { get; init; } = 22f;
    /// <summary>Additional indentation per nested list level.</summary>
    public float NestedListIndent { get; init; }
    /// <summary>True to underline text.</summary>
    public bool Underline { get; init; }
    /// <summary>True to draw a strikethrough decoration.</summary>
    public bool Strikethrough { get; init; }
    /// <summary>Outer margin.</summary>
    public Thickness Margin { get; init; }
    /// <summary>Inner padding.</summary>
    public Thickness Padding { get; init; }
    /// <summary>Line-height multiplier.</summary>
    public float LineHeightMultiplier { get; init; } = 1.4f;
}
