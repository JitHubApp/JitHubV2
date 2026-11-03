using System;
using System.Collections.ObjectModel;

namespace MarkdownRenderer.Theming;

/// <summary>A selector and immutable partial style declaration.</summary>
public sealed class MarkdownStyleRule
{
    /// <summary>Initializes a style rule.</summary>
    public MarkdownStyleRule(MarkdownStyleSelector selector, ElementStyleOverride style)
    {
        Selector = selector ?? throw new ArgumentNullException(nameof(selector));
        Style = style ?? throw new ArgumentNullException(nameof(style));
        if (style.RelativeFontSizeScale is float relativeScale)
        {
            if (!float.IsFinite(relativeScale) || relativeScale <= 0f || relativeScale > 1f)
                throw new ArgumentOutOfRangeException(nameof(style), "A relative font-size scale must be finite and in (0, 1].");
            if (style.FontSize.HasValue)
                throw new ArgumentException("A style rule cannot declare both absolute and relative font size.", nameof(style));
            if (selector.Role.Name is not (MarkdownElementKeys.Subscript or MarkdownElementKeys.Superscript or MarkdownElementKeys.CodeInline))
                throw new ArgumentException("Relative font size is supported only for subscript, superscript, and inline code roles.", nameof(selector));
        }
    }

    /// <summary>Gets the selector.</summary>
    public MarkdownStyleSelector Selector { get; }
    /// <summary>Gets the partial style declaration.</summary>
    public ElementStyleOverride Style { get; }
}
