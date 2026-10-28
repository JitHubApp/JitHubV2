using System;

namespace MarkdownRenderer.Theming;

/// <summary>Style properties addressable through role-specific app resources.</summary>
public enum MarkdownStyleProperty
{
    /// <summary>Primary foreground brush.</summary>
    ForegroundBrush,
    /// <summary>Hovered foreground brush.</summary>
    HoverForegroundBrush,
    /// <summary>Keyboard-focused foreground brush.</summary>
    FocusForegroundBrush,
    /// <summary>Background brush.</summary>
    BackgroundBrush,
    /// <summary>Accent brush.</summary>
    AccentBrush,
    /// <summary>Border brush.</summary>
    BorderBrush,
    /// <summary>Font family.</summary>
    FontFamily,
    /// <summary>Font size.</summary>
    FontSize,
    /// <summary>Font weight.</summary>
    FontWeight,
    /// <summary>Font style.</summary>
    FontStyle,
    /// <summary>Outer margin.</summary>
    Margin,
    /// <summary>Inner padding.</summary>
    Padding,
    /// <summary>Border thickness.</summary>
    BorderThickness,
    /// <summary>Corner radius.</summary>
    CornerRadius,
    /// <summary>Line-height multiplier.</summary>
    LineHeightMultiplier,
    /// <summary>Base list indentation.</summary>
    ListIndent,
    /// <summary>Additional nested-list indentation.</summary>
    NestedListIndent,
    /// <summary>Text decorations.</summary>
    TextDecorations
}

/// <summary>
/// Stable keys used to override MarkdownRenderer visuals through WinUI resources.
/// </summary>
public static class MarkdownResourceKeys
{
    /// <summary>Prefix reserved for renderer-owned resource keys.</summary>
    public const string Prefix = "MarkdownRenderer.";

    /// <summary>Document surface brush.</summary>
    public const string DocumentSurfaceBrush = Prefix + "Document.SurfaceBrush";
    /// <summary>Selected-text background brush.</summary>
    public const string SelectionBackgroundBrush = Prefix + "Selection.BackgroundBrush";
    /// <summary>Selected-text foreground brush.</summary>
    public const string SelectionForegroundBrush = Prefix + "Selection.ForegroundBrush";
    /// <summary>Keyboard focus visual brush.</summary>
    public const string FocusVisualBrush = Prefix + "FocusVisual.Brush";
    /// <summary>Document padding.</summary>
    public const string DocumentPadding = Prefix + "Document.Padding";
    /// <summary>Default spacing between adjacent blocks.</summary>
    public const string BlockSpacing = Prefix + "Document.BlockSpacing";
    /// <summary>Minimum touch target size for renderer commands.</summary>
    public const string MinimumInteractiveSize = Prefix + "Interaction.MinimumSize";
    /// <summary>Local horizontal-overflow indicator brush for code, table, and vector blocks.</summary>
    public const string OverflowIndicatorBrush = Prefix + "Overflow.IndicatorBrush";

    /// <summary>Body text foreground brush.</summary>
    public const string BodyForegroundBrush = Prefix + "Body.ForegroundBrush";
    /// <summary>Body text font family.</summary>
    public const string BodyFontFamily = Prefix + "Body.FontFamily";
    /// <summary>Body text font size.</summary>
    public const string BodyFontSize = Prefix + "Body.FontSize";
    /// <summary>Link foreground brush.</summary>
    public const string LinkForegroundBrush = Prefix + "Link.ForegroundBrush";
    /// <summary>Hovered link foreground brush.</summary>
    public const string LinkHoverForegroundBrush = Prefix + "Link.HoverForegroundBrush";
    /// <summary>Code block background brush.</summary>
    public const string CodeBlockBackgroundBrush = Prefix + "CodeBlock.BackgroundBrush";
    /// <summary>Code text font family.</summary>
    public const string CodeFontFamily = Prefix + "CodeBlock.FontFamily";
    /// <summary>Quote accent brush.</summary>
    public const string QuoteAccentBrush = Prefix + "Quote.AccentBrush";
    /// <summary>Table border brush.</summary>
    public const string TableBorderBrush = Prefix + "Table.BorderBrush";
    /// <summary>Table cell padding.</summary>
    public const string TableCellPadding = Prefix + "TableCell.Padding";

    /// <summary>Returns the stable resource key for a role-specific style property.</summary>
    public static string ForRole(MarkdownStyleRole role, MarkdownStyleProperty property)
    {
        if (role.IsEmpty)
            throw new ArgumentException("A style role is required.", nameof(role));

        return Prefix + role.Name + "." + PropertyName(property);
    }

    private static string PropertyName(MarkdownStyleProperty property)
        => property switch
        {
            MarkdownStyleProperty.ForegroundBrush => "ForegroundBrush",
            MarkdownStyleProperty.HoverForegroundBrush => "HoverForegroundBrush",
            MarkdownStyleProperty.FocusForegroundBrush => "FocusForegroundBrush",
            MarkdownStyleProperty.BackgroundBrush => "BackgroundBrush",
            MarkdownStyleProperty.AccentBrush => "AccentBrush",
            MarkdownStyleProperty.BorderBrush => "BorderBrush",
            MarkdownStyleProperty.FontFamily => "FontFamily",
            MarkdownStyleProperty.FontSize => "FontSize",
            MarkdownStyleProperty.FontWeight => "FontWeight",
            MarkdownStyleProperty.FontStyle => "FontStyle",
            MarkdownStyleProperty.Margin => "Margin",
            MarkdownStyleProperty.Padding => "Padding",
            MarkdownStyleProperty.BorderThickness => "BorderThickness",
            MarkdownStyleProperty.CornerRadius => "CornerRadius",
            MarkdownStyleProperty.LineHeightMultiplier => "LineHeightMultiplier",
            MarkdownStyleProperty.ListIndent => "ListIndent",
            MarkdownStyleProperty.NestedListIndent => "NestedListIndent",
            MarkdownStyleProperty.TextDecorations => "TextDecorations",
            _ => throw new ArgumentOutOfRangeException(nameof(property))
        };

    internal static bool IsStylePropertyName(string value) => value is
        "ForegroundBrush" or
        "HoverForegroundBrush" or
        "FocusForegroundBrush" or
        "BackgroundBrush" or
        "AccentBrush" or
        "BorderBrush" or
        "FontFamily" or
        "FontSize" or
        "FontWeight" or
        "FontStyle" or
        "Margin" or
        "Padding" or
        "BorderThickness" or
        "CornerRadius" or
        "LineHeightMultiplier" or
        "ListIndent" or
        "NestedListIndent" or
        "TextDecorations";

    internal static bool IsGlobalResourceKey(string value) => value is
        DocumentSurfaceBrush or
        SelectionBackgroundBrush or
        SelectionForegroundBrush or
        FocusVisualBrush or
        DocumentPadding or
        BlockSpacing or
        MinimumInteractiveSize or
        OverflowIndicatorBrush;

    internal static bool TryGetStyleRoleName(string resourceKey, out string roleName)
    {
        roleName = string.Empty;
        if (string.IsNullOrEmpty(resourceKey) ||
            !resourceKey.StartsWith(Prefix, StringComparison.Ordinal) ||
            IsGlobalResourceKey(resourceKey))
        {
            return false;
        }

        int propertySeparator = resourceKey.LastIndexOf('.');
        int roleStart = Prefix.Length;
        if (propertySeparator <= roleStart || propertySeparator == resourceKey.Length - 1)
            return false;
        if (!IsStylePropertyName(resourceKey[(propertySeparator + 1)..]))
            return false;

        string candidate = resourceKey[roleStart..propertySeparator];
        if (MarkdownStyleRole.TryCreateCanonical(candidate, out MarkdownStyleRole role))
        {
            roleName = role.Name;
            return true;
        }

        // Resource dictionaries are host-owned input. Malformed or reserved
        // role keys are ignored instead of aborting snapshot construction.
        return false;
    }
}
