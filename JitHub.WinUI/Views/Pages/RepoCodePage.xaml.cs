using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
        private const string PublicPreviewOwner = "JitHubApp";
        private const string PublicPreviewRepository = "JitHubV2";
        private const string PublicPreviewBranch = "main";
        private const long PublicPreviewRepositoryId = 623352671;
        private static readonly IReadOnlyDictionary<string, string> PublicPreviewFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["README.md"] = """
                # JitHubV2

                JitHub is a native WinUI GitHub client for Windows. The preview capture uses this public repository to show the real code experience without relying on GitHub API rate limits.

                ## Highlights

                - Multiple repository tabs
                - Pull request conversations
                - Embedded code browsing
                - Light and dark theme support
                """,
            ["JitHub.WinUI/App.xaml.cs"] = """
                using Microsoft.UI.Xaml;

                namespace JitHub.WinUI;

                public partial class App : Application
                {
                    public App()
                    {
                        InitializeComponent();
                    }
                }
                """,
            ["JitHub.WinUI/Views/Pages/ShellPage.xaml.cs"] = """
                using Microsoft.UI.Xaml.Controls;

                namespace JitHub.WinUI.Views.Pages;

                public sealed partial class ShellPage : Page
                {
                    public ShellPage()
                    {
                        InitializeComponent();
                    }
                }
                """,
            ["JitHub.Web/Pages/Home.razor"] = """
                @page "/"

                <section class="hero">
                    <img src="ss1.png" alt="JitHub app home" />
                    <h1>JitHub</h1>
                    <p>A native GitHub client for Windows.</p>
                </section>
                """,
            ["JitHub.Web/wwwroot/css/site.css"] = """
                :root {
                    color-scheme: light dark;
                    --accent: #2563eb;
                }

                .hero {
                    min-height: 100vh;
                    display: grid;
                    place-items: center;
                }
                """,
            ["JitHub.WinUI/JitHub.WinUI.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
                    <UseWinUI>true</UseWinUI>
                  </PropertyGroup>
                </Project>
                """
        };
        private static readonly string[] PublicPreviewDirectories =
        [
            "JitHub.WinUI",
            "JitHub.WinUI/Views",
            "JitHub.WinUI/Views/Pages",
            "JitHub.Web",
            "JitHub.Web/Pages",
            "JitHub.Web/wwwroot",
            "JitHub.Web/wwwroot/css"
        ];
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
        private readonly Queue<MemoryStream> _previewResponseStreams = new();

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

            if (GitHubClientService.IsPublicAccessToken(_editorAccessToken))
            {
                if (string.Equals(args.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    args.Response = CreateTextResponse(string.Empty, "text/plain", 204, "No Content");
                    return;
                }

                if (TryCreatePublicPreviewResponse(uri, out string responseBody, out string contentType))
                {
                    args.Response = CreateTextResponse(responseBody, contentType);
                    return;
                }
            }

            string authorizationHeader = GetRequestHeader(args.Request.Headers, "Authorization");
            if (authorizationHeader.Contains(HostGitHubTokenMarker, StringComparison.Ordinal))
            {
                if (GitHubClientService.IsPublicAccessToken(_editorAccessToken))
                {
                    args.Request.Headers.RemoveHeader("Authorization");
                    return;
                }

                args.Request.Headers.SetHeader(
                    "Authorization",
                    authorizationHeader.Replace(HostGitHubTokenMarker, _editorAccessToken, StringComparison.Ordinal));
                return;
            }

            string? markerToken = GetTokenQueryParameter(uri);
            if (string.Equals(markerToken, HostGitHubTokenMarker, StringComparison.Ordinal))
            {
                if (GitHubClientService.IsPublicAccessToken(_editorAccessToken))
                {
                    return;
                }

                args.Request.Headers.SetHeader("Authorization", $"token {_editorAccessToken}");
            }
        }

        private CoreWebView2WebResourceResponse CreateTextResponse(
            string body,
            string contentType,
            int statusCode = 200,
            string reasonPhrase = "OK")
        {
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
            _previewResponseStreams.Enqueue(stream);
            while (_previewResponseStreams.Count > 40)
            {
                _previewResponseStreams.Dequeue().Dispose();
            }

            return ShellWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                stream.AsRandomAccessStream(),
                statusCode,
                reasonPhrase,
                $"Content-Type: {contentType}; charset=utf-8\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Allow-Methods: GET, OPTIONS\r\n" +
                "Access-Control-Allow-Headers: authorization, content-type, accept, x-github-api-version\r\n" +
                "Cache-Control: no-store\r\n");
        }

        private static bool TryCreatePublicPreviewResponse(Uri uri, out string responseBody, out string contentType)
        {
            responseBody = string.Empty;
            contentType = "application/json";

            string[] pathSegments = Uri.UnescapeDataString(uri.AbsolutePath)
                .Trim('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            {
                if (pathSegments.Length >= 4 &&
                    IsPublicPreviewRepository(pathSegments[0], pathSegments[1]))
                {
                    string filePath = string.Join('/', pathSegments[3..]);
                    responseBody = GetPublicPreviewFileContent(filePath);
                    contentType = GetContentType(filePath);
                    return true;
                }

                return false;
            }

            if (pathSegments.Length < 3 ||
                !string.Equals(pathSegments[0], "repos", StringComparison.OrdinalIgnoreCase) ||
                !IsPublicPreviewRepository(pathSegments[1], pathSegments[2]))
            {
                return false;
            }

            if (pathSegments.Length == 3)
            {
                responseBody = SerializePublicPreviewRepository();
                return true;
            }

            if (pathSegments.Length >= 5 &&
                string.Equals(pathSegments[3], "branches", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pathSegments[4], PublicPreviewBranch, StringComparison.OrdinalIgnoreCase))
            {
                responseBody = JsonSerializer.Serialize(new
                {
                    name = PublicPreviewBranch,
                    commit = new
                    {
                        sha = "preview-main-sha",
                        url = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepository}/commits/preview-main-sha"
                    },
                    protected_branch = false
                });
                return true;
            }

            if (pathSegments.Length >= 7 &&
                string.Equals(pathSegments[3], "git", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(pathSegments[4], "ref", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pathSegments[4], "refs", StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(pathSegments[5], "heads", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pathSegments[6], PublicPreviewBranch, StringComparison.OrdinalIgnoreCase))
            {
                responseBody = JsonSerializer.Serialize(new
                {
                    @ref = $"refs/heads/{PublicPreviewBranch}",
                    node_id = "preview-ref-main",
                    url = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepository}/git/ref/heads/{PublicPreviewBranch}",
                    @object = new
                    {
                        sha = "preview-main-sha",
                        type = "commit",
                        url = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepository}/git/commits/preview-main-sha"
                    }
                });
                return true;
            }

            if (pathSegments.Length >= 6 &&
                string.Equals(pathSegments[3], "git", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pathSegments[4], "matching-refs", StringComparison.OrdinalIgnoreCase))
            {
                responseBody = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        @ref = $"refs/heads/{PublicPreviewBranch}",
                        node_id = "preview-matching-ref-main",
                        url = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepository}/git/ref/heads/{PublicPreviewBranch}",
                        @object = new
                        {
                            sha = "preview-main-sha",
                            type = "commit",
                            url = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepository}/git/commits/preview-main-sha"
                        }
                    }
                });
                return true;
            }

            if (pathSegments.Length >= 6 &&
                string.Equals(pathSegments[3], "git", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pathSegments[4], "trees", StringComparison.OrdinalIgnoreCase))
            {
                responseBody = SerializePublicPreviewTree(GetTreePath(pathSegments), IsRecursiveTreeRequest(uri));
                return true;
            }

            if (pathSegments.Length >= 5 &&
                string.Equals(pathSegments[3], "contents", StringComparison.OrdinalIgnoreCase))
            {
                string contentPath = string.Join('/', pathSegments[4..]);
                responseBody = SerializePublicPreviewContents(contentPath);
                return true;
            }

            if (pathSegments.Length >= 4 &&
                string.Equals(pathSegments[3], "readme", StringComparison.OrdinalIgnoreCase))
            {
                responseBody = SerializePublicPreviewFile("README.md");
                return true;
            }

            if (pathSegments.Length >= 5 &&
                string.Equals(pathSegments[3], "commits", StringComparison.OrdinalIgnoreCase))
            {
                responseBody = JsonSerializer.Serialize(new
                {
                    sha = "preview-main-sha",
                    html_url = $"https://github.com/{PublicPreviewOwner}/{PublicPreviewRepository}/commit/preview-main-sha",
                    commit = new
                    {
                        message = "Refresh website screenshots",
                        author = new { name = "JitHub", date = "2026-05-04T17:10:00Z" }
                    }
                });
                return true;
            }

            return false;
        }

        private static bool IsPublicPreviewRepository(string owner, string repo)
        {
            return string.Equals(owner, PublicPreviewOwner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(repo, PublicPreviewRepository, StringComparison.OrdinalIgnoreCase);
        }

        private static string SerializePublicPreviewRepository()
        {
            return JsonSerializer.Serialize(new
            {
                id = PublicPreviewRepositoryId,
                node_id = "R_kgDOJTgCXw",
                name = PublicPreviewRepository,
                full_name = $"{PublicPreviewOwner}/{PublicPreviewRepository}",
                @private = false,
                html_url = $"https://github.com/{PublicPreviewOwner}/{PublicPreviewRepository}",
                description = "GitHub WinUI Client",
                fork = false,
                language = "C#",
                stargazers_count = 146,
                watchers_count = 146,
                forks_count = 15,
                open_issues_count = 8,
                default_branch = PublicPreviewBranch,
                owner = new
                {
                    login = PublicPreviewOwner,
                    id = 170190931,
                    avatar_url = "https://avatars.githubusercontent.com/u/170190931?v=4",
                    html_url = $"https://github.com/{PublicPreviewOwner}"
                }
            });
        }

        private static string SerializePublicPreviewTree(string requestedPath, bool recursive)
        {
            var treeEntries = CreatePublicPreviewTreeEntries(requestedPath, recursive).ToList();

            return JsonSerializer.Serialize(new
            {
                sha = string.IsNullOrWhiteSpace(requestedPath) ? "preview-root-tree" : $"preview-tree-{requestedPath.GetHashCode():x}",
                url = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepository}/git/trees/preview-root-tree",
                tree = treeEntries,
                truncated = false
            });
        }

        private static IEnumerable<object> CreatePublicPreviewTreeEntries(string requestedPath, bool recursive)
        {
            string normalizedPath = requestedPath.Trim('/');
            string prefix = string.IsNullOrWhiteSpace(normalizedPath) ? string.Empty : normalizedPath + "/";
            var allPaths = PublicPreviewDirectories
                .Select(path => (Path: path, Type: "tree"))
                .Concat(PublicPreviewFiles.Keys.Select(path => (Path: path, Type: "blob")))
                .Where(item => string.IsNullOrWhiteSpace(prefix) ||
                    item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            if (!recursive)
            {
                allPaths = allPaths
                    .Select(item => (Path: string.IsNullOrWhiteSpace(prefix) ? item.Path : item.Path[prefix.Length..], item.Type))
                    .Where(item => item.Path.Length > 0 && !item.Path.Contains('/'))
                    .Select(item => (Path: string.IsNullOrWhiteSpace(prefix) ? item.Path : prefix + item.Path, item.Type));
            }

            foreach ((string path, string type) in allPaths)
            {
                string responsePath = string.IsNullOrWhiteSpace(prefix) ? path : path[prefix.Length..];
                if (string.IsNullOrWhiteSpace(responsePath))
                {
                    continue;
                }

                yield return new
                {
                    path = responsePath,
                    mode = type == "tree" ? "040000" : "100644",
                    type,
                    sha = type == "tree" ? $"preview-tree-{path.GetHashCode():x}" : $"preview-blob-{path.GetHashCode():x}",
                    size = type == "blob" ? Encoding.UTF8.GetByteCount(GetPublicPreviewFileContent(path)) : (int?)null,
                    url = type == "tree"
                        ? $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepository}/git/trees/preview-tree"
                        : $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepository}/git/blobs/preview-blob"
                };
            }
        }

        private static string GetTreePath(string[] pathSegments)
        {
            if (pathSegments.Length < 6)
            {
                return string.Empty;
            }

            string treeSpecifier = string.Join('/', pathSegments[5..]);
            int separatorIndex = treeSpecifier.IndexOf(':');
            return separatorIndex >= 0 && separatorIndex < treeSpecifier.Length - 1
                ? treeSpecifier[(separatorIndex + 1)..].Trim('/')
                : string.Empty;
        }

        private static bool IsRecursiveTreeRequest(Uri uri) =>
            uri.Query.Contains("recursive=true", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Contains("recursive=1", StringComparison.OrdinalIgnoreCase);

        private static string SerializePublicPreviewContents(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return JsonSerializer.Serialize(PublicPreviewDirectories
                    .Where(directory => !directory.Contains('/'))
                    .Select(CreateDirectoryContentItem)
                    .Concat(PublicPreviewFiles.Keys
                        .Where(filePath => !filePath.Contains('/'))
                        .Select(CreateFileContentItem)));
            }

            if (PublicPreviewFiles.ContainsKey(path))
            {
                return SerializePublicPreviewFile(path);
            }

            string prefix = path.TrimEnd('/') + "/";
            var children = PublicPreviewDirectories
                .Where(directory => directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(directory => directory[prefix.Length..])
                .Where(rest => rest.Length > 0 && !rest.Contains('/'))
                .Select(child => CreateDirectoryContentItem(prefix + child))
                .Concat(PublicPreviewFiles.Keys
                    .Where(filePath => filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(filePath => filePath[prefix.Length..])
                    .Where(rest => rest.Length > 0 && !rest.Contains('/'))
                    .Select(child => CreateFileContentItem(prefix + child)))
                .ToList();

            return JsonSerializer.Serialize(children);
        }

        private static object CreateDirectoryContentItem(string path)
        {
            string name = path.Split('/').Last();
            return new
            {
                type = "dir",
                name,
                path,
                sha = $"preview-tree-{path.GetHashCode():x}",
                url = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepository}/contents/{Uri.EscapeDataString(path)}?ref={PublicPreviewBranch}",
                html_url = $"https://github.com/{PublicPreviewOwner}/{PublicPreviewRepository}/tree/{PublicPreviewBranch}/{path}",
                git_url = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepository}/git/trees/preview-tree"
            };
        }

        private static object CreateFileContentItem(string path)
        {
            string name = path.Split('/').Last();
            return new
            {
                type = "file",
                name,
                path,
                sha = $"preview-blob-{path.GetHashCode():x}",
                size = Encoding.UTF8.GetByteCount(GetPublicPreviewFileContent(path)),
                url = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepository}/contents/{Uri.EscapeDataString(path)}?ref={PublicPreviewBranch}",
                html_url = $"https://github.com/{PublicPreviewOwner}/{PublicPreviewRepository}/blob/{PublicPreviewBranch}/{path}",
                git_url = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepository}/git/blobs/preview-blob",
                download_url = $"https://raw.githubusercontent.com/{PublicPreviewOwner}/{PublicPreviewRepository}/{PublicPreviewBranch}/{path}"
            };
        }

        private static string SerializePublicPreviewFile(string path)
        {
            string content = GetPublicPreviewFileContent(path);
            string encodedContent = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
            string name = path.Split('/').Last();
            return JsonSerializer.Serialize(new
            {
                type = "file",
                encoding = "base64",
                size = Encoding.UTF8.GetByteCount(content),
                name,
                path,
                content = encodedContent,
                sha = $"preview-blob-{path.GetHashCode():x}",
                url = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepository}/contents/{Uri.EscapeDataString(path)}?ref={PublicPreviewBranch}",
                html_url = $"https://github.com/{PublicPreviewOwner}/{PublicPreviewRepository}/blob/{PublicPreviewBranch}/{path}",
                git_url = $"https://api.github.com/repos/{PublicPreviewOwner}/{PublicPreviewRepository}/git/blobs/preview-blob",
                download_url = $"https://raw.githubusercontent.com/{PublicPreviewOwner}/{PublicPreviewRepository}/{PublicPreviewBranch}/{path}"
            });
        }

        private static string GetPublicPreviewFileContent(string path)
        {
            return PublicPreviewFiles.TryGetValue(path.TrimStart('/'), out string? content)
                ? content
                : "namespace JitHub.Preview;\n\npublic static class Placeholder\n{\n    public const string Message = \"Preview file loaded.\";\n}\n";
        }

        private static string GetContentType(string filePath)
        {
            if (filePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                return "text/markdown";
            }

            if (filePath.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            {
                return "text/css";
            }

            if (filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return "application/json";
            }

            return "text/plain";
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




