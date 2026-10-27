//MIT, 2016-present, WinterDev

using Typography.OpenFont;
using Typography.OpenFont.CFF;

namespace Typography.Contours;

/// <summary>Minimal CFF-only outline builder used by the renderer adapter.</summary>
internal abstract class GlyphOutlineBuilderBase
{
    private readonly CffEvaluationEngine _evaluationEngine = new();
    private Cff1GlyphData? _glyph;
    private float _scale;

    protected GlyphOutlineBuilderBase(Typeface typeface)
    {
        Typeface = typeface ?? throw new ArgumentNullException(nameof(typeface));
    }

    public Typeface Typeface { get; }

    public void BuildFromGlyph(Glyph glyph, float sizeInPoints)
    {
        ArgumentNullException.ThrowIfNull(glyph);
        _glyph = glyph.CffInfo;
        _scale = Typeface.CalculateScaleToPixel(Typeface.ConvPointsToPixels(sizeInPoints));
    }

    public void ReadShapes(IGlyphTranslator translator)
    {
        ArgumentNullException.ThrowIfNull(translator);
        Cff1GlyphData glyph = _glyph
            ?? throw new InvalidOperationException($"{nameof(BuildFromGlyph)} must be called first.");
        _evaluationEngine.Run(translator, glyph.GlyphInstructions, _scale);
    }
}
