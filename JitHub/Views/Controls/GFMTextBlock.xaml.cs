using JitHub.Helpers;
using JitHub.Services;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Toolkit.Uwp.UI.Helpers;
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls
{
    public sealed partial class GFMTextBlock : UserControl
    {
        private ThemeListener _themeListener = new ThemeListener();
        private IMarkdownService _markdownService;

        public static DependencyProperty BodyProperty = DependencyProperty.Register(
            nameof(Body),
            typeof(string),
            typeof(GFMTextBlock),
            new PropertyMetadata(default(string), OnBodyChange));

        private static void OnBodyChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is GFMTextBlock self && e.NewValue != null)
            {
                self.Markdown = (string)e.NewValue;
                self.DisplayHTML(self.Markdown);
            }
        }

        public string Body
        {
            get => (string)GetValue(BodyProperty);
            set
            {
                SetValue(BodyProperty, value);
            }
        }

        
        public string Markdown { get; set; }
        public GFMTextBlock()
        {
            this.InitializeComponent();
            Markdown = "";
            _markdownService = Ioc.Default.GetService<IMarkdownService>();
            SizeChanged += GFMTextBlock_SizeChanged;
            _themeListener.ThemeChanged += Listener_ThemeChanged;
            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            SizeChanged -= GFMTextBlock_SizeChanged;
            _themeListener.ThemeChanged -= Listener_ThemeChanged;
        }

        private void GFMTextBlock_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            BodyWebView.HandleResize();
        }

        public void DisplayHTML(string markdown)
        {
            var html = _markdownService.ParseGFM(markdown, _themeListener.CurrentTheme);
            BodyWebView.NavigateToString(html);
        }

        private async void BodyWebView_NavigationStarting(WebView sender, WebViewNavigationStartingEventArgs args)
        {
            if (args.Uri != null)
            {
                args.Cancel = true;
                await Windows.System.Launcher.LaunchUriAsync(args.Uri);
            }
        }

        private void Listener_ThemeChanged(ThemeListener sender)
        {
            DisplayHTML(Markdown);
        }

        private void BodyWebView_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            var webView = sender as WebView;
            webView.HandleResize();
        }
    }
}
