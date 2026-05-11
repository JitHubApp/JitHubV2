using System.Linq;
using System.Text;
using Markdig.Extensions.Footnotes;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Gfm.Renderers;

/// <summary>
/// Renders the footnote definitions section (<see cref="FootnoteGroup"/>) at the bottom
/// of the document. Each footnote is a <see cref="ListItemBox"/> with a superscript marker
/// and a ↩ back-link that scrolls to the inline citation.
/// </summary>
public sealed class FootnoteRenderer : MarkdownNodeRenderer<FootnoteGroup>
{
    public override BlockBox? BuildBlock(FootnoteGroup group, MarkdownLayoutContext context)
    {
        var stack = new StackBox
        {
            Margin = new Thickness(0, 8, 0, 8),
        };
        stack.BlockIndex = context.NextBlockIndex();

        // Collect all Markdig-assigned orders so fallback values never collide with them.
        var assignedOrders = new System.Collections.Generic.HashSet<int>(
            group.OfType<Footnote>().Where(f => f.Order > 0).Select(f => f.Order));
        int fallbackOrder = 0;
        foreach (var item in group)
        {
            if (item is not Footnote footnote) continue;

            int order;
            if (footnote.Order > 0)
            {
                order = footnote.Order;
            }
            else
            {
                // Footnote has no Markdig-assigned order; derive a unique fallback
                // value by skipping any integer already used by assigned footnotes.
                do { fallbackOrder++; } while (assignedOrders.Contains(fallbackOrder));
                order = fallbackOrder;
            }

            // Superscript index marker — no explicit ElementKey so it inherits Body style.
            string superscript = ToSuperscript(order);
            var marker = new InlineContainerBox(context, MarkdownElementKeys.ListMarker);
            marker.BlockIndex = context.NextBlockIndex();
            marker.Add(new TextRun(superscript + " ")
            {
                SourceSpan = new MarkdownRenderer.SourceSpan(footnote.Span.Start, 0)
            });

            // Content area.
            var content = new StackBox();
            content.BlockIndex = context.NextBlockIndex();
            GfmChildBuilder.PopulateChildren(content, footnote, context);

            // Back-link: append a ↩ link INLINE at the end of the last paragraph
            // in the footnote content, so it appears on the same line rather than
            // on its own line. We look for the last InlineContainerBox child
            // (recursively through nested StackBoxes) and append a space + ↩ run.
            var backLinkRun = new LinkRun("↩", $"#footnote-ref-{order}")
            {
                SourceSpan = new MarkdownRenderer.SourceSpan(footnote.Span.Start, 0),
            };
            if (!TryAppendToLastInlineBox(content, backLinkRun))
            {
                // Fallback: create a new InlineContainerBox for the back-link.
                var backLinkBox = new InlineContainerBox(context, MarkdownElementKeys.Body);
                backLinkBox.BlockIndex = context.NextBlockIndex();
                backLinkBox.Add(backLinkRun);
                content.Add(backLinkBox);
            }

            var listItem = new ListItemBox(marker, content, markerWidth: 22f);
            listItem.BlockIndex = context.NextBlockIndex();
            stack.Add(listItem);

            // Register the marker's block index as the definition target so
            // clicking [^1] in the body scrolls to the top of this list item.
            context.RegisterFootnoteDef(order, marker.BlockIndex);
        }

        return stack;
    }

    /// <summary>
    /// Recursively finds the last <see cref="InlineContainerBox"/> in a
    /// <see cref="StackBox"/> tree and appends a space + the given run to it.
    /// Returns false if no eligible container was found.
    /// </summary>
    private static bool TryAppendToLastInlineBox(StackBox stack, LinkRun run)
    {
        // Walk children in reverse to find the last InlineContainerBox.
        for (int i = stack.Children.Count - 1; i >= 0; i--)
        {
            var child = stack.Children[i];
            if (child is InlineContainerBox icb)
            {
                // Append a non-breaking space + the back-link run.
                // Use the back-link run's SourceSpan for the synthetic space so the
                // source map maps it to the footnote definition site, not document start.
                icb.Add(new TextRun("\u00A0") { SourceSpan = run.SourceSpan });
                icb.Add(run);
                return true;
            }
            if (child is StackBox nested && TryAppendToLastInlineBox(nested, run))
                return true;
            // ListItemBox.Content is a StackBox — recurse into it.
            if (child is ListItemBox lib && TryAppendToLastInlineBox(lib.Content, run))
                return true;
        }
        return false;
    }

    private static string ToSuperscript(int n)
    {
        const string digits = "\u2070\u00B9\u00B2\u00B3\u2074\u2075\u2076\u2077\u2078\u2079";
        var sb = new StringBuilder();
        foreach (char c in n.ToString())
            sb.Append(c >= '0' && c <= '9' ? digits[c - '0'] : c);
        return sb.ToString();
    }
}
