using HtmlAgilityPack;
using Markdig.Syntax;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Documents;

namespace Markdig.UWP.TextElements;

internal class MyHeading : IAddChild
{
    private Paragraph _paragraph;
    private HeadingBlock _headingBlock;
    private HtmlNode _htmlNode;

    public bool IsHtml => _htmlNode != null;

    public TextElement TextElement
    {
        get => _paragraph;
    }

    public MyHeading(HeadingBlock headingBlock)
    {
        _headingBlock = headingBlock;
        _paragraph = new Paragraph();
        
        var level = headingBlock.Level;
        _paragraph.FontSize = 24 - (level * 2);
        _paragraph.Foreground = Extensions.GetAccentColorBrush();
        _paragraph.FontWeight = level == 1 ? FontWeights.Bold : FontWeights.Normal;
    }

    public MyHeading(HtmlNode htmlNode)
    {
        _htmlNode = htmlNode;
        _paragraph = new Paragraph();

        var align = _htmlNode.GetAttributeValue("align", "left");
        _paragraph.TextAlignment = align switch
        {
            "left" => Microsoft.UI.Xaml.TextAlignment.Left,
            "right" => Microsoft.UI.Xaml.TextAlignment.Right,
            "center" => Microsoft.UI.Xaml.TextAlignment.Center,
            "justify" => Microsoft.UI.Xaml.TextAlignment.Justify,
            _ => Microsoft.UI.Xaml.TextAlignment.Left,
        };

        var level = int.Parse(htmlNode.Name.Substring(1));
        _paragraph.FontSize = 24 - (level * 2);
        _paragraph.Foreground = Extensions.GetAccentColorBrush();
        _paragraph.FontWeight = level == 1 ? FontWeights.Bold : FontWeights.Normal;
    }

    public void AddChild(IAddChild child)
    {
        if (child.TextElement is Inline inlineChild)
        {
            _paragraph.Inlines.Add(inlineChild);
        }
    }
}
