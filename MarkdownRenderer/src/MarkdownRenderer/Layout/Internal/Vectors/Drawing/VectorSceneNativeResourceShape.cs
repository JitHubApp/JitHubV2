namespace MarkdownRenderer.Layout.Internal.Vectors.Drawing;

/// <summary>Native slot counts, stack depths and admission weight of one vector scene.</summary>
internal readonly struct VectorSceneNativeResourceShape(
    int geometries, int textLayouts, int strokeStyles, int transformDepth, int clipDepth, long weightUnits)
{
    internal int Geometries { get; } = geometries;
    internal int TextLayouts { get; } = textLayouts;
    internal int StrokeStyles { get; } = strokeStyles;
    internal int TransformDepth { get; } = transformDepth;
    internal int ClipDepth { get; } = clipDepth;
    internal long WeightUnits { get; } = weightUnits;
}
