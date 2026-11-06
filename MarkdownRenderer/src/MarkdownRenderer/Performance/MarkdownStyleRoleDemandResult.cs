namespace MarkdownRenderer.Performance;

internal sealed class MarkdownStyleRoleDemandResult(
    ulong mask,
    bool isComplete,
    string? firstIncompleteNodeType)
{
    internal ulong Mask { get; } = mask;
    internal bool IsComplete { get; } = isComplete;
    internal string? FirstIncompleteNodeType { get; } = firstIncompleteNodeType;
}
