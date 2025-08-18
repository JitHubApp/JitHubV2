using CommunityToolkit.Labs.WinUI.MarkdownTextBlock;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Services;
using System.ComponentModel;
using System.Windows.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Common
{
    public sealed partial class MarkdownForm : UserControl
    {

        private IGitHubService _gitHubService;

        public static DependencyProperty ActionContentProperty = DependencyProperty.Register(
            nameof(ActionContent),
            typeof(FrameworkElement),
            typeof(MarkdownForm),
            new PropertyMetadata(default(FrameworkElement), null));

        public static DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(MarkdownForm),
            new PropertyMetadata(default(string), null));
        public static DependencyProperty FormPaddingProperty = DependencyProperty.Register(
            nameof(FormPadding),
            typeof(Thickness),
            typeof(MarkdownForm),
            new PropertyMetadata(new Thickness(0), null));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set
            {
                SetValue(TextProperty, value);
            }
        }

        public FrameworkElement ActionContent
        {
            get => (FrameworkElement)GetValue(ActionContentProperty);
            set
            {
                SetValue(ActionContentProperty, value);
            }
        }

        public Thickness FormPadding
        {
            get => (Thickness)GetValue(FormPaddingProperty);
            set => SetValue(FormPaddingProperty, value);
        }

        public MarkdownConfig Config => _gitHubService.GetMarkdownConfig();

        public MarkdownForm()
        {
            this.InitializeComponent();
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.Text = Text;
            _gitHubService = Ioc.Default.GetService<IGitHubService>();
        }
    }
}
