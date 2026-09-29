//MIT, 2016-present, WinterDev
//MIT, 2015, Michael Popoloski
//FTL, 3-clauses BSD, FreeType project

using Typography.OpenFont.CFF;

namespace Typography.OpenFont;

/// <summary>Receives vector outline operations from the CFF evaluator.</summary>
internal interface IGlyphTranslator
{
    void BeginRead(int contourCount);

    void EndRead();

    void MoveTo(float x0, float y0);

    void LineTo(float x1, float y1);

    void Curve3(float x1, float y1, float x2, float y2);

    void Curve4(float x1, float y1, float x2, float y2, float x3, float y3);

    void CloseContour();
}

internal static class GlyphReaderExtensions
{
    public static void Read(this IGlyphTranslator translator, Cff1GlyphData glyphData, float scale = 1)
    {
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(glyphData);
        new CffEvaluationEngine().Run(translator, glyphData.GlyphInstructions, scale);
    }
}
