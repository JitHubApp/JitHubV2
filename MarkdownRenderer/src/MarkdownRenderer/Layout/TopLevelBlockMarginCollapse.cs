using System.Collections.Generic;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

internal static class TopLevelBlockMarginCollapse
{
    internal static void ResetLeadingTopMargin(List<BlockBox> blocks)
    {
        if (blocks.Count > 0)
            blocks[0].SuppressTopMargin = true;
    }

    internal static void Apply(List<BlockBox> blocks)
    {
        for (int index = 1; index < blocks.Count; index++)
        {
            BlockBox previous = blocks[index - 1];
            BlockBox current = blocks[index];
            if (StackBox.SharesSafeHtmlInlineFlow(previous, current))
                continue;

            current.CollapsedTopMargin = StackBox.GetPositiveCollapsedMargin(
                previous.Margin.Bottom,
                current.Margin.Top);
        }
    }

    internal static void ResetTrailingBottomMargin(List<BlockBox> blocks)
    {
        if (blocks.Count > 0)
        {
            BlockBox last = blocks[blocks.Count - 1];
            if (last is StackBox stack)
                stack.RestoreCollapsedTrailingChildMargin();
            last.SuppressBottomMargin = true;
        }
    }
}
