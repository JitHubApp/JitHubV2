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
