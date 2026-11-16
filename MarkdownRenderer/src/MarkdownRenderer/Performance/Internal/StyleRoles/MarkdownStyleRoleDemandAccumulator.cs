namespace MarkdownRenderer.Performance.Internal.StyleRoles;

/// <summary>
/// The style-role mask and completeness of one demand collection. The first
/// node that can request roles the walk cannot see marks it incomplete.
/// </summary>
internal sealed class MarkdownStyleRoleDemandAccumulator
{
    internal MarkdownStyleRoleDemand Demands { get; private set; } = MarkdownStyleRoleDemand.Body;
    internal bool IsComplete { get; private set; } = true;
    internal string? FirstIncompleteNodeType { get; private set; }

    internal void AddCodeBlockDemand()
        => Add(MarkdownStyleRoleDemand.CodeBlock |
            MarkdownStyleRoleDemand.CodeBlockHeader |
            MarkdownStyleRoleDemand.CodeBlockLanguage |
            MarkdownStyleRoleDemand.CodeBlockGutter |
            MarkdownStyleRoleDemand.CodeBlockLineNumber);

    internal void AddTableDemand()
        => Add(MarkdownStyleRoleDemand.Table | MarkdownStyleRoleDemand.TableHeader | MarkdownStyleRoleDemand.TableCell);

    internal void Add(MarkdownStyleRoleDemand demand) => Demands |= demand;

    internal void MarkIncomplete(object source)
    {
        if (!IsComplete)
            return;

        IsComplete = false;
        FirstIncompleteNodeType = source.GetType().FullName;
    }
}
