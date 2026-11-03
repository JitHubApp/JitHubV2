using System.Collections.Generic;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Html.Renderers;

/// <summary>
/// Builds a code block box for a safe-HTML preformatted element.
/// </summary>
internal static class HtmlPreformattedBlock
{
    internal static BlockBox? BuildPreformatted(
        SafeHtmlElement pre,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        HtmlPreformattedTextProjection? textProjection = HtmlPreformattedTextProjection.Create(
            pre,
            context,
            sourceOffset);
        if (textProjection is null || textProjection.Text.Length == 0)
        {
            return null;
        }

        string text = textProjection.Text;
        SourceSpan sourceSpan = textProjection.SourceSpan;
        CodeBlockMetadata metadata = CodeBlockMetadata.FromDeclarative(
            sourceSpan,
            text,
            language: null,
            new Dictionary<string, string>());
        var codeBlock = new CodeBlockBox(
            context,
            metadata,
            text,
            context.IsCodeBlockCopyEnabled,
            showLineNumbers: false)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        context.RegisterConstructionBlock(codeBlock);
        var chunk = new InlineContainerBox(context, MarkdownElementKeys.CodeBlock)
        {
            BlockIndex = context.NextBlockIndex(),
            TextAlignment = HtmlAlignments.ToCanvasAlignment(alignment),
            CodeBlockTextOffset = 0,
            CodeBlockTextLength = text.Length,
        };
        chunk.Add(new TextRun(text) { SourceSpan = sourceSpan }, textProjection.SourceProjection!);
        context.RegisterConstructionBlock(chunk);
        codeBlock.AddChunk(chunk);
        context.AdoptConstructionBlock(chunk);
        return codeBlock;
    }
}
