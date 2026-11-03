using CSharpMath.Atom;
using CSharpMath.Rendering.FrontEnd;

namespace MarkdownRenderer.Math.Internal;

internal sealed class MathSceneDimensionException : Exception
{
    internal MathSceneDimensionException(string dimension)
        : base($"The formula exceeded its {dimension} budget.")
    {
        Dimension = dimension;
    }

    internal string Dimension { get; }
}
