using System;
using Microsoft.Graphics.Canvas.Text;
using MarkdownRenderer.Layout.Internal.Text;

namespace MarkdownRenderer.Layout.Internal.Inlines;

/// <summary>
/// Moves a line break that DirectWrite placed inside a glued symbol token
/// (for example "C+" / "+ application") back to the space before that token,
/// the way a browser wraps it.
/// </summary>
/// <remarks>
/// DirectWrite offers no way to remove a break opportunity from a text
/// layout, and inserting a word joiner would shift every UTF-16 offset the
/// box shares with selection, hit testing and accessibility. Instead, once
/// the styled layout has wrapped, each line that ends at a junction
/// <see cref="GluedTokenBreakRule"/> forbids gets a mandatory break on the
/// space that starts the token's word, and the layout wraps again. Lines are
/// repaired in order because each repair reflows only the lines after it.
/// When no space precedes the token on its line, the word alone is wider
/// than the line: a browser would overflow there, so DirectWrite's emergency
/// break is kept. The result depends only on the layout's text, styles and
/// width, so the painted and selection layouts wrap identically.
/// </remarks>
internal static class InlineGluedTokenWrapping
{
    /// <summary>
    /// Repairs every line of <paramref name="layout"/> that wraps inside a
    /// glued token of <paramref name="box"/>'s layout text.
    /// </summary>
    /// <param name="box">The inline container whose layout text was laid out.</param>
    /// <param name="layout">The styled wrapping layout of that text.</param>
    /// <param name="observeCancellation">Whether to observe the build cancellation token between repairs.</param>
    internal static void Apply(InlineContainerBox box, CanvasTextLayout layout, bool observeCancellation = true)
    {
        if (box.WordWrapping == CanvasWordWrapping.NoWrap)
            return;

        string text = box.LayoutBuffer;
        if (!GluedTokenBreakRule.ContainsGluedJunction(text))
            return;

        InlineForcedLineBreakObject? forcedBreak = null;
        int searchFrom = 0;
        while (true)
        {
            if (observeCancellation)
                box.ThrowIfCancellationRequested();

            CanvasLineMetrics[] lines;
            try
            {
                lines = layout.LineMetrics;
            }
            catch (NotSupportedException)
            {
                // Some WinRT projections cannot marshal line metrics. The
                // paragraph keeps DirectWrite's own wrapping, as it did before.
                return;
            }

            int space = FindRepairableSpace(text, lines, searchFrom);
            if (space < 0)
                return;

            layout.SetInlineObject(space, 1, forcedBreak ??= new InlineForcedLineBreakObject());
            searchFrom = space + 1;
        }
    }

    // Returns the space to break after for the first line at or after
    // searchFrom that ends inside a glued token, or -1 when none can move.
    private static int FindRepairableSpace(string text, CanvasLineMetrics[] lines, int searchFrom)
    {
        int lineStart = 0;
        for (int line = 0; line < lines.Length - 1; line++)
        {
            int lineEnd = lineStart + lines[line].CharacterCount;
            if (lineEnd <= 0 || lineEnd >= text.Length)
                return -1;

            if (lineEnd > searchFrom &&
                lines[line].TerminalNewlineCount == 0 &&
                GluedTokenBreakRule.ForbidsBreakBetween(text[lineEnd - 1], text[lineEnd]))
            {
                int space = FindWordStartSpace(text, Math.Max(lineStart, searchFrom), lineEnd);
                if (space >= 0)
                    return space;
            }

            lineStart = lineEnd;
        }

        return -1;
    }

    // The last space before lineEnd that still leaves content on the line.
    private static int FindWordStartSpace(string text, int lineStart, int lineEnd)
    {
        for (int index = lineEnd - 1; index > lineStart; index--)
        {
            if (text[index] == ' ')
                return index;
        }

        return -1;
    }
}
