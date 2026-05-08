using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MarkdownRenderer.Theming;

/// <summary>
/// Identifies a markdown element category for theme lookups. Custom extensions
/// may use any string key.
/// </summary>
public static class MarkdownElementKeys
{
    public const string Body = "Body";
    public const string Heading1 = "Heading1";
    public const string Heading2 = "Heading2";
    public const string Heading3 = "Heading3";
    public const string Heading4 = "Heading4";
    public const string Heading5 = "Heading5";
    public const string Heading6 = "Heading6";
    public const string CodeInline = "CodeInline";
    public const string CodeBlock = "CodeBlock";
    public const string Quote = "Quote";
    public const string Link = "Link";
    public const string Strong = "Strong";
    public const string Emphasis = "Emphasis";
    public const string Strikethrough = "Strikethrough";
    public const string ListMarker = "ListMarker";
    public const string ThematicBreak = "ThematicBreak";

    // GFM extension keys
    public const string TableHeader = "TableHeader";
    public const string TableCell = "TableCell";
    public const string AlertNote = "AlertNote";
    public const string AlertTip = "AlertTip";
    public const string AlertImportant = "AlertImportant";
    public const string AlertWarning = "AlertWarning";
    public const string AlertCaution = "AlertCaution";
}

public sealed class ElementStyle
{
    /// <summary>
    /// DirectWrite font fallback chain. Include Segoe UI Emoji for emoji support.
    /// </summary>
    public string FontFamily { get; init; } = "Segoe UI Variable, Segoe UI Emoji, Segoe UI Symbol";
    public float FontSize { get; init; } = 14f;
    public Windows.UI.Text.FontWeight FontWeight { get; init; } = Microsoft.UI.Text.FontWeights.Normal;
    public Windows.UI.Text.FontStyle FontStyle { get; init; } = Windows.UI.Text.FontStyle.Normal;
    public Color Foreground { get; init; } = Microsoft.UI.Colors.Black;
    public Color? Background { get; init; }
    public Color? AccentBar { get; init; }
    public bool Underline { get; init; }
    public bool Strikethrough { get; init; }
    public Thickness Margin { get; init; }
    public Thickness Padding { get; init; }
    public float LineHeightMultiplier { get; init; } = 1.4f;
}

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
    public string? FontFamily { get; init; }
    public float? FontSize { get; init; }
    public Windows.UI.Text.FontWeight? FontWeight { get; init; }
    public Windows.UI.Text.FontStyle? FontStyle { get; init; }
    public Color? Foreground { get; init; }
    public Color? Background { get; init; }
    public Color? AccentBar { get; init; }
    public bool? Underline { get; init; }
    public bool? Strikethrough { get; init; }
    public Thickness? Margin { get; init; }
    public Thickness? Padding { get; init; }
    public float? LineHeightMultiplier { get; init; }
}
