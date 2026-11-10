using System;

namespace MarkdownRenderer.Extensions.Vectors;

/// <summary>Font and positioning attributes for an already-resolved text line.</summary>
public sealed class MarkdownVectorTextStyle
{
    /// <summary>Creates a resolved text style.</summary>
    /// <param name="fontFamily">The font-family name.</param>
    /// <param name="fontSize">The font size in scene units.</param>
    /// <param name="fontWeight">The OpenType weight from 1 through 999.</param>
    /// <param name="italic">Whether the line uses italic styling.</param>
    /// <param name="rightToLeft">Whether the resolved line direction is right-to-left.</param>
    /// <param name="alignment">How the line is positioned relative to its baseline origin.</param>
    /// <param name="fontRole">
    /// Whether the host keeps <paramref name="fontFamily"/> or resolves the family
    /// from the containing markdown style role.
    /// </param>
    public MarkdownVectorTextStyle(
        string fontFamily,
        float fontSize,
        ushort fontWeight = 400,
        bool italic = false,
        bool rightToLeft = false,
        MarkdownVectorTextAlignment alignment = MarkdownVectorTextAlignment.Start,
        MarkdownVectorFontRole fontRole = MarkdownVectorFontRole.Authored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);
        if (!float.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (fontWeight is < 1 or > 999)
            throw new ArgumentOutOfRangeException(nameof(fontWeight));
        if (!Enum.IsDefined(alignment))
            throw new ArgumentOutOfRangeException(nameof(alignment));
        if (!Enum.IsDefined(fontRole))
            throw new ArgumentOutOfRangeException(nameof(fontRole));

        FontFamily = fontFamily.Trim();
        FontSize = fontSize;
        FontWeight = fontWeight;
        Italic = italic;
        RightToLeft = rightToLeft;
        Alignment = alignment;
        FontRole = fontRole;
    }

    /// <summary>Gets the resolved font-family name.</summary>
    public string FontFamily { get; }
    /// <summary>Gets how the font family is resolved by the host.</summary>
    public MarkdownVectorFontRole FontRole { get; }
    /// <summary>Gets the resolved font size in scene units.</summary>
    public float FontSize { get; }
    /// <summary>Gets the OpenType weight from 1 through 999.</summary>
    public ushort FontWeight { get; }
    /// <summary>Gets whether the line uses italic styling.</summary>
    public bool Italic { get; }
    /// <summary>Gets whether the resolved line direction is right-to-left.</summary>
    public bool RightToLeft { get; }
    /// <summary>Gets how the line is positioned relative to its baseline origin.</summary>
    public MarkdownVectorTextAlignment Alignment { get; }
}
