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
/// of the document. Each footnote is a <see cref="ListItemBox"/> with a superscript marker.
/// </summary>
public sealed class FootnoteRenderer : MarkdownNodeRenderer<FootnoteGroup>
{
    public override BlockBox? BuildBlock(FootnoteGroup group, MarkdownLayoutContext context)
    {
        var stack = new StackBox
        {
            Margin = new Thickness(0, 8, 0, 8),
        };

        foreach (var item in group)
        {
            if (item is not Footnote footnote) continue;

            // Superscript index marker — no explicit ElementKey so it inherits Body style.
            string superscript = ToSuperscript(footnote.Order > 0 ? footnote.Order : 1);
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

            var listItem = new ListItemBox(marker, content, markerWidth: 22f);
            listItem.BlockIndex = context.NextBlockIndex();
            stack.Add(listItem);
        }

        return stack;
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
