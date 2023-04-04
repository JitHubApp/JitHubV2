using System;
using System.Collections.Generic;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.PullRequest
{
    public sealed partial class DiffTextBlock : UserControl
    {
        public static DependencyProperty PatchProperty = DependencyProperty.Register(
            nameof(Patch),
            typeof(string),
            typeof(DiffTextBlock),
            new PropertyMetadata(default(string), OnPatchChange)
        );

        private static void OnPatchChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DiffTextBlock self && e.NewValue != null)
            {
                var patch = (string)e.NewValue;
                var lines = patch.Split('\n');
                self.MyBlock.Text = patch;
                self.MyBlock.TextHighlighters.Clear();
                int currChar = 0;
                var addRange = new List<TextRange>();
                var removeRange = new List<TextRange>();
                foreach (var line in lines)
                {
                    if (line.StartsWith('+'))
                    {
                        addRange.Add(new TextRange { StartIndex = currChar, Length = line.Length });
                    }
                    else if (line.StartsWith('-'))
                    {
                        removeRange.Add(new TextRange { StartIndex = currChar, Length = line.Length });
                    }
                    currChar += line.Length + 1;
                }
                //self.SetDiff(lines);
                var addHighlighter = new TextHighlighter();
                var removeHighlighter = new TextHighlighter();
                var greenBrush = new SolidColorBrush(Colors.Green);
                var redBrush = new SolidColorBrush(Colors.Red);
                addHighlighter.Foreground = greenBrush;
                addHighlighter.Background = new SolidColorBrush(Colors.Transparent);
                removeHighlighter.Foreground = redBrush;
                removeHighlighter.Background = new SolidColorBrush(Colors.Transparent);
                foreach (var range in addRange)
                {
                    addHighlighter.Ranges.Add(range);
                }
                foreach (var range in removeRange)
                {
                    removeHighlighter.Ranges.Add(range);
                }
                self.MyBlock.TextHighlighters.Add(addHighlighter);
                self.MyBlock.TextHighlighters.Add(removeHighlighter);
            }
        }


        public string Patch
        {
            get => (string)GetValue(PatchProperty);
            set
            {
                SetValue(PatchProperty, value);
            }
        }
        //public ObservableCollection<FrameworkElement> Lines { get; set; }
        public DiffTextBlock()
        {
            this.InitializeComponent();
            //Lines = new ObservableCollection<FrameworkElement>();
        }

        //public void SetDiff(string[] patches)
        //{
        //    //var overflows = new Stack<RichTextBlockOverflow>();
        //    //for (var i = 0; i < patches.Length; i++)
        //    //{
        //    //    Container.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
        //    //    var grid = new Grid();
        //    //    Grid.SetRow(grid, i);
        //    //    if (i == 0)
        //    //    {
        //    //        var text = new RichTextBlock();
        //    //        var paragraph = new Paragraph { Inlines = { new Run { Text = patches[i] } } };
        //    //        text.Blocks.Add(paragraph);
        //    //        if (patches.Length > 1)
        //    //        {
        //    //            var overflow = new RichTextBlockOverflow();
        //    //            text.OverflowContentTarget = overflow;
        //    //            overflows.Push(overflow);
        //    //        }
        //    //        grid.Children.Add(text);
        //    //    }
        //    //    else
        //    //    {
        //    //        var next = new RichTextBlockOverflow();
        //    //        var prev = overflows.Pop();
        //    //        prev.OverflowContentTarget = next;
        //    //        grid.Children.Add(prev);
        //    //        overflows.Push(next);
        //    //    }
        //    //    if (patches[i].StartsWith('-'))
        //    //    {
        //    //        grid.Background = new SolidColorBrush(Colors.Red);
        //    //    }
        //    //    else if (patches[i].StartsWith('+'))
        //    //    {
        //    //        grid.Background = new SolidColorBrush(Colors.Green);
        //    //    }
        //    //    Container.Children.Add(grid);
        //    //}

        //    for (var i = 0; i < patches.Length; i ++)
        //    {
        //        if (i == 0)
        //        {
        //            var text = new RichTextBlock();
        //            var paragraph = new Paragraph { Inlines = { new Run { Text = patches[i] } } };
        //            text.Blocks.Add(paragraph);
        //            Lines.Add(text);
        //        }
        //        else
        //        {
        //            var overflow = new RichTextBlockOverflow();
        //            if (i == 1)
        //            {
        //                ((RichTextBlock)Lines[i - 1]).OverflowContentTarget = overflow;
        //            }
        //            else
        //            {
        //                ((RichTextBlockOverflow)(Lines[i - 1])).OverflowContentTarget = overflow;
        //            }
        //            Lines.Add(overflow);
        //        }
        //    }
        //}
    }
}
