using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// The rules for how an inline run projects into the semantic document's
/// accessible text: its text, whether it is atomic, whether it can be read
/// as an exact source slice, and where a word boundary must be inserted.
/// </summary>
internal static class MarkdownSemanticInlineText
{
    internal static string GetInlineAccessibleText(
        InlineRun run,
        MarkdownLayoutContext context) =>
        run is InlineImageRun imageRun
            ? MarkdownImageSemanticPolicy.GetInlineAccessibleName(
                imageRun.AltText,
                imageRun.IsLinked,
                imageRun.LinkTitle,
                context.ResolveString(
                    MarkdownStringKeys.ImageName,
                    MarkdownLocalizedStrings.ImageName)) ?? string.Empty
            : run.AccessibleText;

    internal static bool IsAtomicInline(InlineRun run) =>
        run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun or InlineSpacerRun;

    internal static bool NeedsSemanticBoundary(char left, char right) =>
        char.IsLetterOrDigit(left) && char.IsLetterOrDigit(right);

    internal static bool TryGetNextInlineFirstCharacter(
        InlineContainerBox inline,
        int startIndex,
        out char firstCharacter)
    {
        for (int index = startIndex; index < inline.Runs.Count; index++)
        {
            InlineRun run = inline.Runs[index];
            if (run is SourceSliceTextRun sourceSlice && IsExactSourceSlice(inline, run, sourceSlice))
            {
                if (sourceSlice.SliceLength == 0)
                    continue;

                firstCharacter = sourceSlice.SourceText[sourceSlice.SourceStart];
                return true;
            }

            string text = GetInlineAccessibleText(run, inline.Context);
            if (text.Length > 0)
            {
                firstCharacter = text[0];
                return true;
            }
        }

        firstCharacter = default;
        return false;
    }

    internal static bool IsExactSourceSlice(
        InlineContainerBox inline,
        InlineRun run,
        SourceSliceTextRun sourceSlice) =>
        ReferenceEquals(sourceSlice.SourceText, inline.Context.SourceMap.SourceText) &&
        sourceSlice.SourceStart == run.SourceSpan.Start &&
        sourceSlice.SliceLength == run.SourceSpan.Length;
}
