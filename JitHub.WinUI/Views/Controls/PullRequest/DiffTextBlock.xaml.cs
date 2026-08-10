using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.PullRequest
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
                var addHighlighter = new TextHighlighter
                {
                    Foreground = GetThemeBrush("AppSuccessBrush")
                };
                var removeHighlighter = new TextHighlighter
                {
                    Foreground = GetThemeBrush("AppDangerBrush")
                };
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
        }

        private static Brush GetThemeBrush(string resourceKey) =>
            Application.Current.Resources.TryGetValue(resourceKey, out object? resource) && resource is Brush brush
                ? brush
                : throw new InvalidOperationException($"Required theme brush '{resourceKey}' is unavailable.");
    }
}

