using Markdig.Syntax;
using Windows.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;

namespace Markdig.UWP.TextElements;

internal class MyQuote : IAddChild
{
    private Paragraph _paragraph;
    private MyFlowDocument _flowDocument;
    private QuoteBlock _quoteBlock;

    public TextElement TextElement
    {
        get => _paragraph;
    }

    public MyQuote(QuoteBlock quoteBlock)
    {
        _quoteBlock = quoteBlock;
        _paragraph = new Paragraph();
        
        _flowDocument = new MyFlowDocument(quoteBlock);
        var inlineUIContainer = new InlineUIContainer();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Auto) });
        grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Auto) });
        
        var bar = new Grid();
        bar.Width = 4;
        bar.Background = new SolidColorBrush(Colors.Gray);
        bar.SetValue(Grid.ColumnProperty, 0);
        bar.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch;
        bar.Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 4, 0);
        grid.Children.Add(bar);

        var rightGrid = new Grid();
        rightGrid.Padding = new Microsoft.UI.Xaml.Thickness(4);
        rightGrid.Children.Add(_flowDocument.RichTextBlock);

        rightGrid.SetValue(Grid.ColumnProperty, 1);
        grid.Children.Add(rightGrid);
        grid.Margin = new Microsoft.UI.Xaml.Thickness(0, 2, 0, 2);

        inlineUIContainer.Child = grid;

        _paragraph.Inlines.Add(inlineUIContainer);
    }

    public void AddChild(IAddChild child)
    {
        _flowDocument.AddChild(child);
    }
}
