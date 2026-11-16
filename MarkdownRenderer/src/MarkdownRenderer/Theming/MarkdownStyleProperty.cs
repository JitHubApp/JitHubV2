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
