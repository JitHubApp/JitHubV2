using CommunityToolkit.Labs.WinUI.MarkdownTextBlock;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

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
