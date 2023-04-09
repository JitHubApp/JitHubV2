using HtmlAgilityPack;
using Markdig.Syntax;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Markdig.UWP.TextElements;

public class MyFlowDocument : IAddChild
{
    private HtmlNode _htmlNode;
    private RichTextBlock _richTextBlock;
    private MarkdownObject _markdownObject;
    
    // useless property
    public TextElement TextElement { get; set; }
    //
    
    public RichTextBlock RichTextBlock
    {
        get => _richTextBlock;
        set => _richTextBlock = value;
    }

    public bool IsHtml => _htmlNode != null;

    public MyFlowDocument()
    {
        RichTextBlock = new RichTextBlock();
    }

    public MyFlowDocument(MarkdownObject markdownObject)
    {
        _markdownObject = markdownObject;
        RichTextBlock = new RichTextBlock();
    }

    public MyFlowDocument(HtmlNode node)
    {
        _htmlNode = node;
        RichTextBlock = new RichTextBlock();
    }

    public void AddChild(IAddChild child)
    {
        TextElement element = child.TextElement;
        if (element != null)
        {
            if (element is Microsoft.UI.Xaml.Documents.Block block)
            {
                _richTextBlock.Blocks.Add(block);
            }
            else if (element is Microsoft.UI.Xaml.Documents.Inline inline)
            {
                var paragraph = new Paragraph();
                paragraph.Inlines.Add(inline);
                _richTextBlock.Blocks.Add(paragraph);
            }
        }
    }
}
