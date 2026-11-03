using System.Numerics;
using CSharpMath.Rendering.FrontEnd;
using CSharpMathPath = CSharpMath.Rendering.FrontEnd.Path;

namespace MarkdownRenderer.Math.Internal;

internal enum MathSceneBudget
{
    Commands,
    PathOperations,
    WorkingMemory,
}
