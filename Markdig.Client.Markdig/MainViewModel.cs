using CommunityToolkit.Mvvm.ComponentModel;
using Markdig.UWP;
using Windows.UI.Xaml.Controls;

namespace Markdig.Client.Markdig
{
    internal partial class MainViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _markdown;

        [ObservableProperty]
        private MarkdownConfig _config;

        public void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = (sender as TextBox).Text;
            Config = new MarkdownConfig()
            {
                Markdown = text,
            };
        }
    }
}
