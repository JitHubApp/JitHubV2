using HtmlAgilityPack;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Markdig.UWP.TextElements.Html;

internal class MyBlock : IAddChild
{
    private HtmlNode _htmlNode;
    private Paragraph _paragraph;
    private List<RichTextBlock> _richTextBlocks;

    public TextElement TextElement
    {
        get => _paragraph;
    }

    public MyBlock(HtmlNode block)
    {
        _htmlNode = block;
        var align = _htmlNode.GetAttributeValue("align", "left");
        _richTextBlocks = new List<RichTextBlock>();
        _paragraph = new Paragraph();
        _paragraph.TextAlignment = align switch
        {
            "left" => Microsoft.UI.Xaml.TextAlignment.Left,
            "right" => Microsoft.UI.Xaml.TextAlignment.Right,
            "center" => Microsoft.UI.Xaml.TextAlignment.Center,
            "justify" => Microsoft.UI.Xaml.TextAlignment.Justify,
            _ => Microsoft.UI.Xaml.TextAlignment.Left,
        };
        StyleBlock();
    }

    public void AddChild(IAddChild child)
    {
        if (child.TextElement is Block blockChild)
        {
            _paragraph.Inlines.Add(new LineBreak());
            var inlineUIContainer = new InlineUIContainer();
            var richTextBlock = new RichTextBlock();
            richTextBlock.Blocks.Add(blockChild);
            inlineUIContainer.Child = richTextBlock;
            _richTextBlocks.Add(richTextBlock);
            _paragraph.Inlines.Add(inlineUIContainer);
            _paragraph.Inlines.Add(new LineBreak());
        }
        else if (child.TextElement is Inline inlineChild)
        {
            _paragraph.Inlines.Add(inlineChild);
        }
    }

    private void StyleBlock()
    {
        switch (_htmlNode.Name.ToLower())
        {
            case "address":
                _paragraph.FontStyle = Windows.UI.Text.FontStyle.Italic;
                foreach (var richTextBlock in _richTextBlocks)
                {
                    richTextBlock.FontStyle = Windows.UI.Text.FontStyle.Italic;
                }
                //_flowDocument.RichTextBlock.Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["AddressBlockStyle"];
                break;
        }
    }
}
