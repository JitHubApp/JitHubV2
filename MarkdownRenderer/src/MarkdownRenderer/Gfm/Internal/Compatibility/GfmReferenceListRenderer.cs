using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace MarkdownRenderer.Gfm.Internal.Compatibility;

// Adapted from Markdig's BSD-2-Clause ListRenderer. The only behavioral
// change is the cmark-gfm reference newline before block-level list-item
// children; tight inline paragraphs intentionally remain on the same line.
internal sealed class GfmReferenceListRenderer : HtmlObjectRenderer<ListBlock>
{
    protected override void Write(HtmlRenderer renderer, ListBlock listBlock)
    {
        renderer.EnsureLine();
        if (renderer.EnableHtmlForBlock)
        {
            if (listBlock.IsOrdered)
            {
                renderer.Write("<ol");
                if (listBlock.BulletType != '1')
                {
                    renderer.Write(" type=\"");
                    renderer.Write(listBlock.BulletType);
                    renderer.Write('"');
                }
                if (listBlock.OrderedStart is not null && listBlock.OrderedStart != "1")
                {
                    renderer.Write(" start=\"");
                    renderer.Write(listBlock.OrderedStart);
                    renderer.Write('"');
                }
                renderer.WriteAttributes(listBlock);
                renderer.WriteLine('>');
            }
            else
            {
                renderer.Write("<ul");
                renderer.WriteAttributes(listBlock);
                renderer.WriteLine('>');
            }
        }

        foreach (Block item in listBlock)
        {
            var listItem = (ListItemBlock)item;
            bool previousImplicit = renderer.ImplicitParagraph;
            renderer.ImplicitParagraph = !listBlock.IsLoose;
            renderer.EnsureLine();
            if (renderer.EnableHtmlForBlock)
            {
                renderer.Write("<li");
                renderer.WriteAttributes(listItem);
                bool staysOnOpeningLine =
                    listItem.Count == 0 ||
                    (!listBlock.IsLoose && listItem[0] is ParagraphBlock);
                if (staysOnOpeningLine)
                    renderer.Write('>');
                else
                    renderer.WriteLine('>');
            }

            renderer.WriteChildren(listItem);
            if (renderer.EnableHtmlForBlock)
                renderer.WriteLine("</li>");
            renderer.EnsureLine();
            renderer.ImplicitParagraph = previousImplicit;
        }

        if (renderer.EnableHtmlForBlock)
            renderer.WriteLine(listBlock.IsOrdered ? "</ol>" : "</ul>");
        renderer.EnsureLine();
    }
}
