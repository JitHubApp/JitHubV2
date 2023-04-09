using System;
using JitHub.Models.NavArgs;
using JitHub.Services;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml;
using CommunityToolkit.WinUI.UI.Helpers;

namespace JitHub.Views.Pages;

public sealed partial class RepoCodePage : Microsoft.UI.Xaml.Controls.Page
{
    private ThemeListener _themeListener = new ThemeListener();
    public RepoCodePage()
    {
        this.InitializeComponent();
        ShellWebView.CoreWebView2Initialized += (sender, args) =>
        {
            ShellWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "jithub.local",
                "Assets/dist",
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow
            );
            ShellWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        };
        WebViewContainer.Visibility = Visibility.Collapsed;
    }

    override protected async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var theme = _themeListener.CurrentTheme == ApplicationTheme.Light ? "light" : "dark";
        var arg = (CodeViewerNavArg)e.Parameter;
        var authService = Ioc.Default.GetService<IAuthService>();
        var token = authService.GetToken(authService.AuthenticatedUser.Id);
        await ShellWebView.EnsureCoreWebView2Async();
        var gitRef = arg.IsBranch ? arg.Branch : arg.GitRef;
        ShellWebView.CoreWebView2.Navigate($"https://jithub.local/index.html?ref={gitRef}&owner={arg.Repo.Owner.Login}&repo={arg.Repo.Name}&token={token}&theme={theme}");
        
        ShellWebView.NavigationCompleted += (sender, args) =>
        {
            //todo: instead of this, we should pass in the callback to the webview and have it call it when it's ready
            System.Threading.Thread.Sleep(200);
            LoadingIndicator.Visibility = Visibility.Collapsed;
            LoadingIndicator.IsActive = false;
            WebViewContainer.Visibility = Visibility.Visible;
        };
    }
}
