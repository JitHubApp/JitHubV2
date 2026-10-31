//Apache2, 2017-present, WinterDev
//Apache2, 2014-2016, Samuel Carlsson, WinterDev

using Typography.OpenFont.CFF;
using Typography.OpenFont.MathGlyphs;

namespace Typography.OpenFont;

/// <summary>Minimal CFF glyph model required by CSharpMath.</summary>
internal sealed class Glyph
{
    internal Glyph(Cff1GlyphData cff1Glyph)
    {
        CffInfo = cff1Glyph ?? throw new ArgumentNullException(nameof(cff1Glyph));
        GlyphIndex = cff1Glyph.GlyphIndex;
    }

    public Bounds Bounds { get; internal set; }

    public ushort GlyphIndex { get; }

    public Cff1GlyphData CffInfo { get; }

    public MathGlyphInfo? MathGlyphInfo { get; internal set; }
}
