namespace MarkdownRenderer.Math.Internal.Compilation;

internal sealed class MathSceneBudgetException : Exception
{
    internal MathSceneBudgetException(MathSceneBudget budget)
        : base($"The formula exceeded the {budget} scene budget.")
    {
        Budget = budget;
    }

    internal MathSceneBudget Budget { get; }
}
