//Apache2, 2017-present, WinterDev
//Apache2, 2014-2016, Samuel Carlsson, WinterDev

using MarkdownRenderer.Math.Internal;
using Typography.OpenFont.CFF;
using Typography.OpenFont.MathGlyphs;
using Typography.OpenFont.Tables;

namespace Typography.OpenFont;

/// <summary>Minimal CFF typeface model required by CSharpMath.</summary>
internal sealed class Typeface
{
    private const int PointsPerInch = 72;
    private readonly HorizontalMetrics _horizontalMetrics;
    private bool _evaluatedCffGlyphBounds;

    internal Typeface(
        Head header,
        MaxProfile maxProfile,
        HorizontalHeader horizontalHeader,
        HorizontalMetrics horizontalMetrics,
        Cmap cmap,
        Glyph[] glyphs,
        CFFTable cffTable)
    {
        Bounds = header.Bounds;
        UnitsPerEm = header.UnitsPerEm;
        MaxProfile = maxProfile;
        HheaTable = horizontalHeader;
        _horizontalMetrics = horizontalMetrics;
        CmapTable = cmap;
        Glyphs = glyphs;
        CffTable = cffTable;
    }

    public Bounds Bounds { get; }

    public ushort UnitsPerEm { get; }

    public short LineGap => HheaTable.LineGap;

    public Glyph[] Glyphs { get; }

    public int GlyphCount => Glyphs.Length;

    internal MaxProfile MaxProfile { get; }

    internal HorizontalHeader HheaTable { get; }

    internal Cmap CmapTable { get; }

    internal CFFTable CffTable { get; }

    internal MathTable? _mathTable;

    internal MathGlyphInfo[]? _mathGlyphInfos;

    public MathConstants? MathConsts => _mathTable?._mathConstTable;

    public ushort GetGlyphIndex(int codepoint) =>
        CmapTable.GetGlyphIndex(codepoint, 0, out _);

    public Glyph GetGlyph(ushort glyphIndex) => Glyphs[glyphIndex];

    public ushort GetHAdvanceWidthFromGlyphIndex(ushort glyphIndex) =>
        _horizontalMetrics.GetAdvanceWidth(glyphIndex);

    public static float ConvPointsToPixels(float targetPointSize, int resolution = 96) =>
        targetPointSize * resolution / PointsPerInch;

    public float CalculateScaleToPixel(float targetPixelSize) =>
        targetPixelSize / UnitsPerEm;

    public float CalculateScaleToPixelFromPointSize(float targetPointSize, int resolution = 96) =>
        (targetPointSize * resolution / PointsPerInch) / UnitsPerEm;

    public bool HasMathTable() => MathConsts is not null;

    public void UpdateAllCffGlyphBounds()
    {
        if (_evaluatedCffGlyphBounds)
            return;

        var engine = new CffEvaluationEngine();
        var finder = new CffBoundsFinder();
        foreach (Glyph glyph in Glyphs)
        {
            MathCancellationScope.Checkpoint();
            Cff1GlyphData data = glyph.CffInfo;
            finder.Reset();
            engine.Run(finder, data.GlyphInstructions);
            glyph.Bounds = finder.GetResultBounds();
        }

        _evaluatedCffGlyphBounds = true;
    }

    private sealed class CffBoundsFinder : IGlyphTranslator
    {
        private float _minimumX;
        private float _maximumX;
        private float _minimumY;
        private float _maximumY;
        private float _currentX;
        private float _currentY;
        private float _moveX;
        private float _moveY;
        private bool _hasPoint;

        public void Reset()
        {
            _minimumX = _minimumY = float.MaxValue;
            _maximumX = _maximumY = float.MinValue;
            _currentX = _currentY = _moveX = _moveY = 0;
            _hasPoint = false;
        }

        public void BeginRead(int contourCount)
        {
        }

        public void EndRead()
        {
        }

        public void MoveTo(float x0, float y0)
        {
            _moveX = _currentX = x0;
            _moveY = _currentY = y0;
            Include(x0, y0);
        }

        public void LineTo(float x1, float y1)
        {
            _currentX = x1;
            _currentY = y1;
            Include(x1, y1);
        }

        public void Curve3(float x1, float y1, float x2, float y2)
        {
            for (int step = 1; step < 3; step++)
            {
                float t = step / 3f;
                float c = 1 - t;
                Include(
                    (c * c * _currentX) + (2 * t * c * x1) + (t * t * x2),
                    (c * c * _currentY) + (2 * t * c * y1) + (t * t * y2));
            }

            _currentX = x2;
            _currentY = y2;
            Include(x2, y2);
        }

        public void Curve4(float x1, float y1, float x2, float y2, float x3, float y3)
        {
            for (int step = 1; step < 3; step++)
            {
                float t = step / 3f;
                float c = 1 - t;
                Include(
                    (_currentX * c * c * c) + (x1 * 3 * t * c * c) + (x2 * 3 * t * t * c) + (x3 * t * t * t),
                    (_currentY * c * c * c) + (y1 * 3 * t * c * c) + (y2 * 3 * t * t * c) + (y3 * t * t * t));
            }

            _currentX = x3;
            _currentY = y3;
            Include(x3, y3);
        }

        public void CloseContour()
        {
            _currentX = _moveX;
            _currentY = _moveY;
            Include(_moveX, _moveY);
        }

        public Bounds GetResultBounds()
        {
            if (!_hasPoint)
                return default;
            return new Bounds(
                checked((short)System.Math.Floor(_minimumX)),
                checked((short)System.Math.Floor(_minimumY)),
                checked((short)System.Math.Ceiling(_maximumX)),
                checked((short)System.Math.Ceiling(_maximumY)));
        }

        private void Include(float x, float y)
        {
            MathCancellationScope.Checkpoint();
            _minimumX = System.Math.Min(_minimumX, x);
            _maximumX = System.Math.Max(_maximumX, x);
            _minimumY = System.Math.Min(_minimumY, y);
            _maximumY = System.Math.Max(_maximumY, y);
            _hasPoint = true;
        }
    }
}
