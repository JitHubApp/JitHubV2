using Markdig.UWP;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.Views.Controls.Common
{
    public sealed partial class MarkdownViewer : UserControl
    {
        public static DependencyProperty ConfigProperty = DependencyProperty.Register(
            nameof(Config),
            typeof(MarkdownConfig),
            typeof(MarkdownViewer),
            null
        );

        public MarkdownConfig Config
        {
            get => (MarkdownConfig)GetValue(ConfigProperty);
            set => SetValue(ConfigProperty, value);
        }

        public MarkdownViewer()
        {
            this.InitializeComponent();
        }
    }
}
