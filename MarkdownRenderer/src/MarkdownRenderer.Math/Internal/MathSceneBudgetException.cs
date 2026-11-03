using System.Numerics;
using CSharpMath.Rendering.FrontEnd;
using CSharpMathPath = CSharpMath.Rendering.FrontEnd.Path;

namespace MarkdownRenderer.Math.Internal;

internal sealed class MathSceneBudgetException : Exception
{
    internal MathSceneBudgetException(MathSceneBudget budget)
        : base($"The formula exceeded the {budget} scene budget.")
    {
        Budget = budget;
    }

    internal MathSceneBudget Budget { get; }
}
