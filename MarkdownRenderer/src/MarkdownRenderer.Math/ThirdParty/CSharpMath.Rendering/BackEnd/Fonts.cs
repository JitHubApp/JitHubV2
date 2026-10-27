using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Typography.OpenFont;

namespace CSharpMath.Rendering.BackEnd {
  internal class Fonts : Display.FrontEnd.IFont<Glyph>, IEnumerable<Typeface> {
    private static readonly SemaphoreSlim GlobalTypefacesGate = new(1, 1);
    private static Typefaces? _globalTypefaces;

    static Typefaces GetGlobalTypefaces() {
      var cached = Volatile.Read(ref _globalTypefaces);
      if (cached != null) return cached;

      var cancellationToken = MarkdownRenderer.Math.Internal.MathCancellationScope.CurrentToken;
      GlobalTypefacesGate.Wait(cancellationToken);
      try {
        cached = _globalTypefaces;
        if (cached != null) return cached;

        var reader = new OpenFontReader();
        Typeface LoadFont(string fileName) {
          MarkdownRenderer.Math.Internal.MathCancellationScope.Checkpoint();
          using var fontStream = MarkdownRenderer.Math.Internal.MathFontResources.Open(fileName);
          var typeface = reader.Read(fontStream);
          typeface.UpdateAllCffGlyphBounds();
          return typeface;
        }
        var initialized = new Typefaces(LoadFont("latinmodern-math.otf"));
        initialized.AddOverride(LoadFont("AMS-Capital-Blackboard-Bold.otf"));
        initialized.AddSupplement(LoadFont("cyrillic-modern-nmr10.otf"));
        Volatile.Write(ref _globalTypefaces, initialized);
        return initialized;
      } finally {
        GlobalTypefacesGate.Release();
      }
    }
    public Fonts(IEnumerable<Typeface> localTypefaces, float pointSize) {
      PointSize = pointSize;
      Typefaces = localTypefaces.Concat(GetGlobalTypefaces());
      MathTypeface = Typefaces.First(t => t.HasMathTable());
      MathConsts = MathTypeface.MathConsts ?? throw new Structures.InvalidCodePathException(nameof(MathTypeface) + " doesn't have " + nameof(MathConsts));
    }
    public float PointSize { get; }
    public IEnumerable<Typeface> Typefaces { get; }
    public Typeface MathTypeface { get; }
    public Typography.OpenFont.MathGlyphs.MathConstants MathConsts { get; }
    public IEnumerator<Typeface> GetEnumerator() => Typefaces.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => Typefaces.GetEnumerator();
  }
}
