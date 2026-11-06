namespace MarkdownRenderer.Math.Internal;

internal sealed class MathParserRecursionLimitException : Exception
{
    internal MathParserRecursionLimitException()
        : base("The formula exceeded the native parser recursion limit.")
    {
    }
}
