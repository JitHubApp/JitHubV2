using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Markdig.UWP;

public sealed partial class MarkdownViewer : UserControl
{
    public static DependencyProperty ConfigProperty = DependencyProperty.Register(
        nameof(Config),
        typeof(MarkdownConfig),
        typeof(MarkdownViewer),
        new PropertyMetadata(null, ConfigChanged)
    );

    public static void ConfigChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownViewer self && e.NewValue != null)
        {
            self.ViewModel.Loading = true;
            self.Config = (MarkdownConfig)e.NewValue;
            var uiElement = MarkdownUIBuilder.Build(self.Config);
            self.MarkdownContainer.Children.Clear();
            self.MarkdownContainer.Children.Add(uiElement);
            self.ViewModel.Loading = false;
        }
    }

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
