using System;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using JitHub.Models.NavArgs;
using JitHub.Services;
using Microsoft.UI.Xaml.Navigation;
using CommunityToolkit.WinUI.UI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace JitHub.WinUI.Views.Pages
{
    public sealed partial class RepoCodePage : Microsoft.UI.Xaml.Controls.Page
    {
        private const string HostGitHubTokenMarker = "__jithub_host_token__";
        private const int HeaderNotFoundHResult = unchecked((int)0x80070490);
        private readonly App _app = (App)Application.Current;
        private readonly ThemeListener _themeListener = new();
        private readonly GlobalViewModel _globalViewModel;
        private readonly IAccountService _accountService;
        private readonly IAuthService _authService;
        private readonly EditorAssetService _editorAssetService;
        private readonly IGitHubClientService _gitHubClientService;
        private string? _editorBootstrapScriptId;
        private bool _gitHubRequestBridgeInitialized;
        private string? _editorAccessToken;

        public RepoCodePage()
        {
            _globalViewModel = _app.GetService<GlobalViewModel>();
            _accountService = _app.GetService<IAccountService>();
            _authService = _app.GetService<IAuthService>();
            _editorAssetService = _app.GetService<EditorAssetService>();
            _gitHubClientService = _app.GetService<IGitHubClientService>();
            this.InitializeComponent();
            ShellWebView.NavigationStarting += OnNavigationStarting;
            ShellWebView.NavigationCompleted += OnNavigationCompleted;
        }

        override protected async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            try
            {
                await InitializeEditorAsync((CodeViewerNavArg)e.Parameter);
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private async Task InitializeEditorAsync(CodeViewerNavArg arg)
        {
            ArgumentNullException.ThrowIfNull(arg);

            SetLoadingState();
            await Task.Yield();

            var theme = _themeListener.CurrentTheme == ApplicationTheme.Light ? "light" : "dark";
            var token = _authService.GetToken(_authService.AuthenticatedUser?.Id ?? _accountService.GetUser());
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("An authenticated GitHub session is required to open the embedded code view.");
            }

            _editorAccessToken = token;
            Task<string> editorAssetPathTask = _editorAssetService.GetEditorRootPathAsync();
            Task ensureWebViewTask = ShellWebView.EnsureCoreWebView2Async().AsTask();
            await Task.WhenAll(editorAssetPathTask, ensureWebViewTask);
            string editorAssetPath = await editorAssetPathTask;

            EnsureGitHubRequestBridge();

            string encodedTokenMarker = JavaScriptEncoder.Default.Encode(HostGitHubTokenMarker);
            if (!string.IsNullOrWhiteSpace(_editorBootstrapScriptId))
            {
                ShellWebView.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(_editorBootstrapScriptId);
            }

            string bootstrapScript =
                "if (window.top === window && window.location && window.location.protocol === 'https:' && window.location.host === 'jithub.local') {" +
                $"Object.defineProperty(window, '__jithubBootstrap', {{ configurable: false, enumerable: false, writable: false, value: Object.freeze({{ githubToken: \"{encodedTokenMarker}\" }}) }});" +
                "}";
            _editorBootstrapScriptId = await ShellWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(bootstrapScript);
            ShellWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "jithub.local",
                editorAssetPath,
                CoreWebView2HostResourceAccessKind.Allow);
            ShellWebView.CoreWebView2.Settings.AreDevToolsEnabled = _globalViewModel.DevMode;
            var repo = arg.Repo ?? throw new InvalidOperationException("Repository context is required to open the embedded code view.");
            var ownerLogin = repo.Owner?.Login;
            if (string.IsNullOrWhiteSpace(ownerLogin) || string.IsNullOrWhiteSpace(repo.Name))
            {
                throw new InvalidOperationException("Repository metadata is incomplete for the embedded code view.");
            }

            string? gitRef = arg.IsBranch ? arg.Branch : arg.GitRef;
            if (string.IsNullOrWhiteSpace(gitRef))
            {
                gitRef = repo.DefaultBranch;
            }

            if (string.IsNullOrWhiteSpace(gitRef))
            {
                var latestRepo = await _gitHubClientService.GetRepositoryAsync(token, ownerLogin, repo.Name);
                if (!string.IsNullOrWhiteSpace(latestRepo.DefaultBranch))
                {
                    repo = latestRepo;
                    arg.WithRepo(latestRepo);
                    gitRef = latestRepo.DefaultBranch;
                }
            }

            if (string.IsNullOrWhiteSpace(gitRef))
            {
                throw new InvalidOperationException("JitHub could not determine which branch to open for this repository.");
            }

            ShellWebView.CoreWebView2.Navigate(
                $"https://jithub.local/index.html?ref={Uri.EscapeDataString(gitRef)}&owner={Uri.EscapeDataString(ownerLogin)}&repo={Uri.EscapeDataString(repo.Name)}&theme={Uri.EscapeDataString(theme)}");
        }

        private async void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            try
            {
                if (!args.IsSuccess)
                {
                    ShowError($"The embedded editor failed to load ({args.WebErrorStatus}).");
                    return;
                }

                await Task.Delay(200);
                LoadingIndicator.Visibility = Visibility.Collapsed;
                LoadingIndicator.IsActive = false;
                ErrorState.Visibility = Visibility.Collapsed;
                WebViewContainer.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private void SetLoadingState()
        {
            ErrorState.Visibility = Visibility.Collapsed;
            ErrorText.Text = string.Empty;
            WebViewContainer.Visibility = Visibility.Collapsed;
            LoadingIndicator.Visibility = Visibility.Visible;
            LoadingIndicator.IsActive = true;
        }

        private void ShowError(string message)
        {
            LoadingIndicator.Visibility = Visibility.Collapsed;
            LoadingIndicator.IsActive = false;
            WebViewContainer.Visibility = Visibility.Collapsed;
            ErrorText.Text = message;
            ErrorState.Visibility = Visibility.Visible;
        }

        private async void OnNavigationStarting(Microsoft.UI.Xaml.Controls.WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            try
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? uri))
                {
                    return;
                }

                if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(uri.Host, "jithub.local", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                args.Cancel = true;
                await Windows.System.Launcher.LaunchUriAsync(uri);
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private void EnsureGitHubRequestBridge()
        {
            if (_gitHubRequestBridgeInitialized)
            {
                return;
            }

            ShellWebView.CoreWebView2.AddWebResourceRequestedFilter("https://api.github.com/*", CoreWebView2WebResourceContext.All);
            ShellWebView.CoreWebView2.AddWebResourceRequestedFilter("https://raw.githubusercontent.com/*", CoreWebView2WebResourceContext.All);
            ShellWebView.CoreWebView2.WebResourceRequested += OnGitHubWebResourceRequested;
            _gitHubRequestBridgeInitialized = true;
        }

        private void OnGitHubWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            if (string.IsNullOrWhiteSpace(_editorAccessToken) ||
                !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out Uri? uri))
            {
                return;
            }

            if (!string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string authorizationHeader = GetRequestHeader(args.Request.Headers, "Authorization");
            if (authorizationHeader.Contains(HostGitHubTokenMarker, StringComparison.Ordinal))
            {
                args.Request.Headers.SetHeader(
                    "Authorization",
                    authorizationHeader.Replace(HostGitHubTokenMarker, _editorAccessToken, StringComparison.Ordinal));
                return;
            }

            string? markerToken = GetTokenQueryParameter(uri);
            if (string.Equals(markerToken, HostGitHubTokenMarker, StringComparison.Ordinal))
            {
                args.Request.Headers.SetHeader("Authorization", $"token {_editorAccessToken}");
            }
        }

        private static string GetRequestHeader(CoreWebView2HttpRequestHeaders headers, string name)
        {
            try
            {
                return headers.GetHeader(name);
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
            catch (COMException ex) when (ex.HResult == HeaderNotFoundHResult)
            {
                return string.Empty;
            }
        }

        private static string? GetTokenQueryParameter(Uri uri)
        {
            if (string.IsNullOrWhiteSpace(uri.Query))
            {
                return null;
            }

            string query = uri.Query.TrimStart('?');
            foreach (string queryPart in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int separatorIndex = queryPart.IndexOf('=');
                string key = separatorIndex >= 0 ? queryPart[..separatorIndex] : queryPart;
                if (!string.Equals(key, "token", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return separatorIndex >= 0 ? Uri.UnescapeDataString(queryPart[(separatorIndex + 1)..]) : string.Empty;
            }

            return null;
        }

    }
}




