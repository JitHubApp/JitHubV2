using System.Globalization;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Accessibility;

/// <summary>Creates deterministic IDs for synthetic UIA peers.</summary>
internal static class MarkdownAutomationIdentity
{
    internal const string Document = "MarkdownDocument";

    internal static string ForBlock(InlineContainerBox box) => Create(
        "Block",
        box.BlockIndex,
        inlineIndex: -1,
        textStart: 0,
        textLength: 0,
        row: -1,
        column: -1);

    internal static string ForRun(string kind, InlineContainerBox box, InlineRun run) => Create(
        kind,
        box.BlockIndex,
        run.InlineIndex,
        run.SourceSpan.Start,
        run.SourceSpan.Length,
        row: -1,
        column: -1);

    internal static string ForCodeCopy(CodeBlockBox box) => Create(
        "CodeCopy",
        box.BlockIndex,
        inlineIndex: -1,
        textStart: 0,
        textLength: 0,
        row: -1,
        column: -1);

    internal static string ForNode(MarkdownSemanticNode node)
    {
        int blockIndex = node.InlineBox?.BlockIndex ??
                         node.Box?.BlockIndex ??
                         node.ImageBox?.BlockIndex ??
                         node.EmbedBox?.BlockIndex ??
                         node.HostedElementBox?.BlockIndex ??
                         0;
        int inlineIndex = node.InlineRun?.InlineIndex ?? -1;
        return Create(
            node.Role.ToString(),
            blockIndex,
            inlineIndex,
            node.TextStart,
            System.Math.Max(0, node.TextEnd - node.TextStart),
            node.Row,
            node.Column);
    }

    private static string Create(
        string kind,
        int blockIndex,
        int inlineIndex,
        int textStart,
        int textLength,
        int row,
        int column) => string.Concat(
            "Markdown",
            kind,
            "-",
            blockIndex.ToString("X8", CultureInfo.InvariantCulture),
            "-",
            inlineIndex.ToString("X8", CultureInfo.InvariantCulture),
            "-",
            textStart.ToString("X8", CultureInfo.InvariantCulture),
            "-",
            textLength.ToString("X8", CultureInfo.InvariantCulture),
            "-",
            row.ToString("X8", CultureInfo.InvariantCulture),
            "-",
            column.ToString("X8", CultureInfo.InvariantCulture));
}
