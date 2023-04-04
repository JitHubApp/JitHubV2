using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;


namespace JitHub.Controls.Editor
{
    public sealed partial class Editor : UserControl
    {
        public static DependencyProperty OptionsProperty = DependencyProperty.Register(
            nameof(Options),
            typeof(EditorOptions),
            typeof(Editor),
            new PropertyMetadata(null, OnOptionsChange)
        );

        private static async void OnOptionsChange(DependencyObject d, DependencyPropertyChangedEventArgs args)
        {
            if (d is Editor self && args.NewValue != null)
            {
                await self.ShellWebView.EnsureCoreWebView2Async();
                self.ShellWebView.CoreWebView2.Navigate($"https://jithub.com/index.html?owner={self.Options.Owner}&repo={self.Options.Repo}&token={self.Options.Token}");
            }
        }
        
        public EditorOptions Options
        {
            get { return (EditorOptions)GetValue(OptionsProperty); }
            set { SetValue(OptionsProperty, value); }
        }
        public Editor()
        {
            this.InitializeComponent();
            ShellWebView.CoreWebView2Initialized += (sender, args) =>
            {
                ShellWebView.CoreWebView2.SetVirtualHostNameToFolderMapping("jithub.com", "Assets/dist", Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            };
        }
    }
}
