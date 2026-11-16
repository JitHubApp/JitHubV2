namespace CSharpMath.Rendering.BackEnd {
  internal static class TypesettingContext {
    public static Display.FrontEnd.TypesettingContext<Fonts, Glyph> Instance { get; } =
      new Display.FrontEnd.TypesettingContext<Fonts, Glyph>(
         (fonts, size) => new Fonts(fonts, size),
         GlyphBoundsProvider.Instance,
         GlyphFinder.Instance,
         MathTable.Instance
       );
  }
}
