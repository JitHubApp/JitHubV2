using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.Services.Markdown;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.CodeViewer;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Pages;
using JitHub.WinUI.Views.Pages.Design;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace JitHub.WinUI;

public partial class App : Application
{
    internal const int DiagnosticsCloseProbeBurstCount = 64;
    internal const string DiagnosticsCloseProbeBurstName = "diagnostics.close.probe.burst";
    internal const string DiagnosticsCloseProbeMarkerName = "diagnostics.close.probe.marker";
    private readonly struct ActivationRequest
    {
        public ActivationRequest(ExtendedActivationKind kind, Uri? protocolUri)
        {
            Kind = kind;
            ProtocolUri = protocolUri;
        }

        public ExtendedActivationKind Kind { get; }

        public Uri? ProtocolUri { get; }
    }

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ApplicationActivationGate _activationGate = new();
    private string? _storedTheme;
    private bool _runtimeMergedDictionariesLoaded;
    private MainWindow? _mainWindow;
    private IServiceProvider? _services;
    private bool _startupTelemetryTracked;
    private Task? _startupSessionRestoreMonitorTask;
    private bool _accountRemovalRecoveryIncomplete;
    private AuthLifecycleAutomationContext? _authLifecycleAutomation;

    public App()
    {
        Program.LogStartupPhase("app.constructor-enter");
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _storedTheme = new ThemeService(new SettingService()).GetTheme();
        ApplyStoredTheme();
        Program.LogStartupPhase("app.constructor-exit");
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Startup is centralized in Program.Main so all activation kinds share one path.
    }

    internal void HandleActivation(AppActivationArguments activationArguments)
    {
        Program.LogStartupPhase($"activation.received:thread-access={_dispatcherQueue.HasThreadAccess}");
        ActivationRequest activationRequest = CreateActivationRequest(activationArguments);

        if (_dispatcherQueue.HasThreadAccess)
        {
            QueueActivation(activationRequest);
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(() => QueueActivation(activationRequest)))
        {
            LogActivationError(new InvalidOperationException("The activation dispatcher is unavailable."));
        }
    }

    private void QueueActivation(ActivationRequest activationRequest)
    {
        try
        {
            _ = GetOrCreateMainWindow();
            _services ??= BuildServices();
            _ = GetService<IApplicationTaskCoordinator>().RunAsync(
                token => _activationGate.RunAsync(
                    innerToken => HandleActivationAsync(activationRequest, innerToken),
                    token),
                new ApplicationTaskOptions("app.activation"));
        }
        catch (Exception exception)
        {
            LogActivationError(exception);
            try
            {
                GetOrCreateMainWindow().ShowActivationError(FormatActivationError(exception));
            }
            catch (Exception recoveryException)
            {
                LogActivationError(new AggregateException(
                    "Activation could not be queued or displayed.",
                    exception,
                    recoveryException));
            }
        }
    }

    private async Task HandleActivationAsync(
        ActivationRequest activationRequest,
        CancellationToken cancellationToken)
    {
        Program.LogStartupPhase("activation.async-enter");
        try
        {
            await ActivateCoreAsync(activationRequest, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Program.LogStartupPhase("activation.async-canceled");
        }
        catch (Exception ex)
        {
            LogActivationError(ex);
            try
            {
                MainWindow mainWindow = GetOrCreateMainWindow();
                mainWindow.ShowActivationError(FormatActivationError(ex));
            }
            catch (Exception recoveryException)
            {
                LogActivationError(new AggregateException(
                    "Activation failed before the main window could be created.",
                    ex,
                    recoveryException));
            }

            throw;
        }
    }

    private async Task ActivateCoreAsync(
        ActivationRequest activationRequest,
        CancellationToken cancellationToken)
    {
        MainWindow mainWindow = GetOrCreateMainWindow();
        Program.LogStartupPhase("activation.core.window-ready");
        _services ??= BuildServices();
        Program.LogStartupPhase("activation.core.services-ready");
        await ResumePendingAccountRemovalAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_startupTelemetryTracked)
        {
            Program.LogStartupPhase("activation.core.telemetry-start");
            GetService<ITelemetryService>().TrackEvent("app.started");
            _startupTelemetryTracked = true;
            Program.LogStartupPhase("activation.core.telemetry-ready");
        }

        InitializeNavigationTargets();
        Program.LogStartupPhase("activation.core.navigation-targets-ready");
        mainWindow.ProcessActivation();
        Program.LogStartupPhase("activation.core.window-activated");
        MarkdownLifecycleAutomationBridge.SignalAppReady();
        if (MarkdownLifecycleAutomationBridge.IsResourceMapForcedAbsent)
        {
            const string fallback = "Resource map fallback active";
            string value = LocalizedResourceText.GetString("Automation.ResourceMapAbsentProbe", fallback);
            if (string.Equals(value, fallback, StringComparison.Ordinal))
            {
                MarkdownLifecycleAutomationBridge.SignalResourceMapFallback(value);
            }
        }

        IAuthService authService = GetService<IAuthService>();
        IAccountService accountService = GetService<IAccountService>();

        if (_accountRemovalRecoveryIncomplete)
        {
            GetService<NavigationService>().Unauthorized();
            mainWindow.ShowActivationError(
                "Local account-data removal is incomplete. JitHub will retry it the next time the app starts.");
            return;
        }

        if (TryHandleLaunchPageOverride())
        {
            Program.LogStartupPhase("activation.core.launch-override-complete");
            return;
        }

        if (activationRequest.Kind == ExtendedActivationKind.Protocol &&
            activationRequest.ProtocolUri is Uri protocolUri)
        {
            if (TryGetAuthProtocolActivationResponse(protocolUri, out string? authResponse))
            {
                bool authorized = await authService.Authorize(authResponse);
                if (authorized)
                {
                    GetService<NavigationService>().GoHome();
                    _authLifecycleAutomation?.Record("protocol.authorization.completed");
                    mainWindow.ShowStatus("GitHub sign-in completed.");
                }
                else if (authService.Authenticated)
                {
                    GetService<NavigationService>().GoHome();
                }
                else if (authService.CheckAuth(accountService.GetUser()))
                {
                    await authService.InitializeAsync();

                    if (authService.Authenticated || authService.CheckAuth(accountService.GetUser()))
                    {
                        GetService<NavigationService>().GoHome();
                    }
                    else
                    {
                        GetService<NavigationService>().Unauthorized();
                    }
                }
                else
                {
                    GetService<NavigationService>().Unauthorized();
                }

                if (!authorized)
                {
                    _authLifecycleAutomation?.Record("protocol.authorization.rejected", authService.RecoveryState.ToString());
                    ShowAuthRecoveryState(authService);
                }

                return;
            }

            StartStartupSessionRestoreIfNeeded();
            NavigateStartupPage();
            return;
        }

        StartStartupSessionRestoreIfNeeded();
        NavigateStartupPage();
    }

    private IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new NavigationService(_mainWindow!.ContentFrameHost));
        services.AddSingleton<DialogPresentationCoordinator>();
        services.AddSingleton<ModalService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IAppStoragePathProvider, AppStoragePathProvider>();
        services.AddSingleton<ILocalDiagnosticsStore, LocalDiagnosticsStore>();
        services.AddSingleton<IStoreTelemetrySink, StoreTelemetrySink>();
        services.AddSingleton<ITelemetryService, TelemetryService>();
        services.AddSingleton<IAccountWorkQuiescence, AccountWorkQuiescence>();
        services.AddSingleton<IApplicationTaskCoordinator, ApplicationTaskCoordinator>();
        services.AddSingleton<IAdaptivePrefetchPolicy, AdaptivePrefetchPolicy>();
        services.AddSingleton<IAccountDataRemovalJournal, AccountDataRemovalJournal>();
        services.AddSingleton<IGitHubCacheStore, SqliteGitHubCacheStore>();
        services.AddSingleton<IGitHubImageCacheStore, GitHubImageCacheStore>();
        services.AddSingleton<IGitHubImageService, GitHubImageService>();
        services.AddSingleton<IMarkdownRemoteImagePolicy, MarkdownRemoteImagePolicy>();
        services.AddSingleton<IGitHubRequestQueue, GitHubRequestQueue>();
        RegisterAuthBoundaries(services);
        services.AddSingleton<IGitHubGraphQlTransport, GitHubGraphQlTransport>();
        services.AddSingleton<IGitHubGraphQlQueryService, GitHubGraphQlQueryService>();
        services.AddSingleton<IGitHubQueryService, GitHubQueryService>();
        services.AddSingleton<IGitHubRepositoryQueryService, GitHubRepositoryQueryService>();
        services.AddSingleton<IGitHubRepositoryIndexService, GitHubRepositoryIndexService>();
        services.AddSingleton<IRepositoryForkOwnershipStore, RepositoryForkOwnershipStore>();
        services.AddSingleton<IGitHubPilotQueryService, GitHubPilotQueryService>();
        services.AddSingleton<IGitHubRepositorySearchQueryService, GitHubRepositorySearchQueryService>();
        services.AddSingleton<IGitHubDashboardQueryService, GitHubDashboardQueryService>();
        services.AddSingleton<IGitHubNotificationQueryService, GitHubNotificationQueryService>();
        services.AddSingleton<IGistMutationJournal, GistMutationJournal>();
        services.AddSingleton<IGitHubGistQueryService, GitHubGistQueryService>();
        services.AddSingleton<NotificationInboxState>();
        services.AddSingleton<IGitHubMeQueryService, GitHubMeQueryService>();
        services.AddSingleton<IGitHubPullRequestQueryService, GitHubPullRequestQueryService>();
        services.AddSingleton<IGitHubIssueQueryService, GitHubIssueQueryService>();
        services.AddSingleton<IGitHubCommitQueryService, GitHubCommitQueryService>();
        services.AddSingleton<JitHub.Services.CodeViewer.IGitHubRepoCodeQueryService, JitHub.Services.CodeViewer.GitHubRepoCodeQueryService>();
        services.AddSingleton<IGitHubProfileQueryService, GitHubProfileQueryService>();
        services.AddSingleton<IStarLibraryStore, SqliteStarLibraryStore>();
        services.AddSingleton<IStarLibraryRecoveryStore, StarLibraryRecoveryStore>();
        services.AddSingleton<IGitHubStarQueryService, GitHubStarQueryService>();
        services.AddSingleton<IGitHubStarLibraryService, GitHubStarLibraryService>();
        services.AddSingleton<IIssueNavigationCache, IssueNavigationCache>();
        services.AddSingleton<IPullRequestNavigationCache, PullRequestNavigationCache>();
        services.AddSingleton<ICommitNavigationCache, CommitNavigationCache>();
        services.AddSingleton<IDashboardWidgetLayoutService, DashboardWidgetLayoutService>();
        services.AddSingleton<ISettingsDiagnosticsService, SettingsDiagnosticsService>();
        services.AddSingleton<ISettingsSourceNavigationService, SettingsSourceNavigationService>();
        services.AddSingleton<IGitHubService, GitHubService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<ISettingsPreferencesService, SettingsPreferencesService>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<IRepoFileCacheService, RepoFileCacheService>();
        services.AddSingleton<ICacheRegistry, CacheRegistry>();
        services.AddSingleton<IAccountDataRemovalCoordinator, AccountDataRemovalCoordinator>();
        services.AddSingleton<ILanguageIdResolver, LanguageIdResolver>();
        services.AddSingleton<IFilePreviewResolver, FilePreviewResolver>();
        services.AddSingleton<JitHub.Services.CodeViewer.IRepoTreeService, JitHub.Services.CodeViewer.RepoTreeService>();
        services.AddSingleton<RepoCodeNavigationPreparationCache>();
        services.AddSingleton<RepositoryRoutePrefetchCoordinator>();
        services.AddSingleton<GlobalViewModel>();
        services.AddTransient<DashboardPageViewModel>();
        services.AddTransient<LoginPageViewModel>();
        services.AddTransient<ProfilePageViewModel>();
        services.AddTransient<RepoSearchResultPageViewModel>();
        services.AddTransient<RepoIssuePageViewModel>();
        services.AddTransient<RepoPullRequestPageViewModel>();
        services.AddTransient<RepoManagePageViewModel>();
        services.AddTransient<RepoCommitsPageViewModel>();
        services.AddTransient<SettingsPageViewModel>();
        services.AddTransient<MyIssuesPageViewModel>();
        services.AddTransient<MyPullRequestsPageViewModel>();
        services.AddSingleton<StarLibrarySessionState>();
        services.AddTransient<StarLibraryPageViewModel>();
        services.AddTransient<GistsPageViewModel>();
        services.AddTransient<NotificationsPageViewModel>();
        services.AddSingleton<ShellPageViewModel>();
        services.AddTransient<RepoCodePageViewModel>();

        IServiceProvider serviceProvider = services.BuildServiceProvider();
        serviceProvider.GetRequiredService<IApplicationTaskCoordinator>().TaskFailed += (_, failure) =>
            RecordBackgroundTaskFailure(serviceProvider, failure);
        Ioc.Default.ConfigureServices(serviceProvider);
        return serviceProvider;
    }

    private void RegisterAuthBoundaries(ServiceCollection services)
    {
        _authLifecycleAutomation = AuthLifecycleAutomationContext.TryCreate(Program.CurrentLaunchOptions);
        if (_authLifecycleAutomation is null)
        {
            services.AddSingleton<IGitHubRestTransport, GitHubRestTransport>();
            services.AddSingleton<IGitHubClientService, GitHubClientService>();
            services.AddSingleton<ISettingService, SettingService>();
            services.AddSingleton<IAppConfig, AppConfig>();
            services.AddSingleton<IAccountService, AccountService>();
            services.AddSingleton<ICredentialVaultBackend, WindowsCredentialVaultBackend>();
            services.AddSingleton<IAuthCredentialStore, AuthCredentialStore>();
            services.AddSingleton<IAuthHandoffClient, AuthHandoffClient>();
            services.AddSingleton<IExternalUriLauncher>(
                IsLoginLaunchFailurePreview()
                    ? new LoginLaunchFailureExternalUriLauncher()
                    : new WindowsExternalUriLauncher());
            return;
        }

        ISettingService settings = new SettingService();
        IAppConfig appConfig = new AppConfig();
        IAccountService account = new AccountService(settings);
        ICredentialVaultBackend credentialBackend = new FileCredentialVaultBackend(_authLifecycleAutomation.CredentialPath);
        IAuthCredentialStore credentials = new AuthCredentialStore(credentialBackend, appConfig);
        _authLifecycleAutomation.Seed(settings, account, credentials);

        services.AddSingleton(settings);
        services.AddSingleton(appConfig);
        services.AddSingleton(account);
        services.AddSingleton(credentialBackend);
        services.AddSingleton(credentials);
        services.AddSingleton(_authLifecycleAutomation.CreateUriLauncher());
        services.AddSingleton<IAuthHandoffClient>(_authLifecycleAutomation.CreateHandoffClient());
        services.AddSingleton<IGitHubClientService>(new GitHubClientService(
            new System.Net.Http.HttpClient(_authLifecycleAutomation.CreateHttpMessageHandler())));
        services.AddSingleton<IGitHubRestTransport>(new GitHubRestTransport(
            new System.Net.Http.HttpClient(_authLifecycleAutomation.CreateHttpMessageHandler())));
    }

    private async Task ResumePendingAccountRemovalAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AccountDataRemovalResult> results = await GetService<IAccountDataRemovalCoordinator>()
            .ResumePendingAsync(cancellationToken)
            .ConfigureAwait(true);
        _accountRemovalRecoveryIncomplete = results.Any(static result => !result.IsComplete);
    }

    private void InitializeNavigationTargets()
    {
        NavigationService navigationService = GetService<NavigationService>();
        navigationService.RootHomePage ??= typeof(ShellPage);
        navigationService.ShellHomePage ??= typeof(DashboardPage);
        navigationService.UnauthorizedPage ??= typeof(LoginPage);
    }

    private void NavigateStartupPage()
    {
        if (TryHandleLaunchPageOverride())
        {
            return;
        }

        if (_mainWindow?.ContentFrameHost.Content is not null)
        {
            return;
        }

        IAuthService authService = GetService<IAuthService>();
        IAccountService accountService = GetService<IAccountService>();
        NavigationService navigationService = GetService<NavigationService>();
        long persistedUserId = accountService.GetUser();

        if (authService.Authenticated || authService.CheckAuth(persistedUserId))
        {
            navigationService.GoHome();
        }
        else
        {
            navigationService.Unauthorized();
        }
    }

    private void StartStartupSessionRestoreIfNeeded()
    {
        if (Program.CurrentLaunchOptions.HasPageOverride)
        {
            return;
        }

        if (_mainWindow?.ContentFrameHost.Content is not null)
        {
            return;
        }

        Task initializeTask = GetService<IAuthService>().InitializeAsync();
        if (initializeTask.IsCompletedSuccessfully)
        {
            ShowAuthRecoveryState(GetService<IAuthService>());
            return;
        }

        if (_startupSessionRestoreMonitorTask is not null &&
            !_startupSessionRestoreMonitorTask.IsCompleted)
        {
            return;
        }

        _startupSessionRestoreMonitorTask = ObserveStartupSessionRestoreAsync(initializeTask);
    }

    private async Task ObserveStartupSessionRestoreAsync(Task initializeTask)
    {
        try
        {
            await initializeTask;
        }
        catch (Exception ex)
        {
            LogActivationError(ex);
            string message = FormatActivationError(ex);
            if (_dispatcherQueue.HasThreadAccess)
            {
                GetOrCreateMainWindow().ShowActivationError(message);
            }
            else
            {
                _ = _dispatcherQueue.TryEnqueue(() => GetOrCreateMainWindow().ShowActivationError(message));
            }

            return;
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            ReconcileStartupNavigationAfterSessionRestore();
        }
        else
        {
            _ = _dispatcherQueue.TryEnqueue(ReconcileStartupNavigationAfterSessionRestore);
        }
    }

    private void ReconcileStartupNavigationAfterSessionRestore()
    {
        if (Program.CurrentLaunchOptions.HasPageOverride)
        {
            return;
        }

        IAuthService authService = GetService<IAuthService>();
        IAccountService accountService = GetService<IAccountService>();

        if (_mainWindow?.ContentFrameHost.Content is null)
        {
            NavigateStartupPage();
        }
        else if (!authService.Authenticated && !authService.CheckAuth(accountService.GetUser()))
        {
            GetService<NavigationService>().Unauthorized();
        }

        ShowAuthRecoveryState(authService);
    }

    private void ShowAuthRecoveryState(IAuthService authService)
    {
        switch (authService.RecoveryState)
        {
            case AuthSessionRecoveryState.Expired:
                _mainWindow?.ShowStatus("Your GitHub session expired. Sign in again to continue.");
                break;
            case AuthSessionRecoveryState.Offline:
                _mainWindow?.ShowStatus("You are offline. JitHub is showing cached account data and will reconnect automatically.");
                break;
            case AuthSessionRecoveryState.ServiceUnavailable:
                _mainWindow?.ShowStatus("GitHub is temporarily unavailable. Cached account data remains available.");
                break;
            case AuthSessionRecoveryState.InvalidCallback:
                _mainWindow?.ShowStatus("GitHub sign-in could not be verified. No token was accepted.");
                break;
        }
    }

    private void ApplyStoredTheme()
    {
        string? themeOverride = Program.CurrentLaunchOptions.Theme;
        if (!string.IsNullOrWhiteSpace(themeOverride))
        {
            RequestedTheme = themeOverride.Equals("dark", StringComparison.OrdinalIgnoreCase)
                ? ApplicationTheme.Dark
                : themeOverride.Equals("light", StringComparison.OrdinalIgnoreCase)
                    ? ApplicationTheme.Light
                    : RequestedTheme;
            return;
        }

        if (!string.IsNullOrWhiteSpace(_storedTheme) &&
            !string.Equals(_storedTheme, ThemeConst.System, StringComparison.Ordinal))
        {
            RequestedTheme = ThemeService.GetApplicationThemeStatic(_storedTheme);
        }
    }

    private void LoadRuntimeMergedDictionaries()
    {
        AddRuntimeMergedDictionary("ms-appx:///Styles/TabViewTheme.xaml");
        AddRuntimeMergedDictionary("ms-appx:///Styles/TabView.xaml");
    }

    private void AddRuntimeMergedDictionary(string source)
    {
        if (Resources.MergedDictionaries.Any(dictionary =>
                string.Equals(dictionary.Source?.OriginalString, source, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(source, UriKind.Absolute)
        });
    }

    private static bool TryGetAuthProtocolActivationResponse(Uri uri, out string response)
    {
        response = string.Empty;

        if (!AuthProtocolPolicy.IsExpectedScheme(uri))
        {
            return false;
        }

        string original = WebUtility.HtmlDecode(uri.OriginalString);
        string normalizedHost = uri.Host.Trim('/');
        string normalizedPath = uri.AbsolutePath.Trim('/');
        string query = uri.Query.TrimStart('?');
        string fragment = uri.Fragment.TrimStart('#');

        bool authEndpoint =
            string.Equals(normalizedHost, "auth", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedPath, "auth", StringComparison.OrdinalIgnoreCase) ||
            IsAuthEndpoint(original);
        bool hasAuthPayload =
            ContainsKeyValue(query, "handoff") ||
            ContainsKeyValue(query, "state") ||
            ContainsKeyValue(fragment, "handoff") ||
            ContainsKeyValue(fragment, "state");

        if (!authEndpoint && !hasAuthPayload)
        {
            return false;
        }

        response = WebUtility.HtmlDecode(CombineKeyValuePayload(query, fragment, original));
        return true;
    }

    private static string CombineKeyValuePayload(string query, string fragment, string? original = null)
    {
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(fragment) && !string.IsNullOrWhiteSpace(original))
        {
            string? originalQuery = TryExtractOriginalComponent(original, '?', '#');
            string? originalFragment = TryExtractOriginalComponent(original, '#');
            query = string.IsNullOrWhiteSpace(originalQuery) ? query : originalQuery;
            fragment = string.IsNullOrWhiteSpace(originalFragment) ? fragment : originalFragment;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return fragment;
        }

        if (string.IsNullOrWhiteSpace(fragment))
        {
            return query;
        }

        return $"{query}&{fragment}";
    }

    private static bool IsAuthEndpoint(string original)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return false;
        }

        int schemeSeparatorIndex = original.IndexOf("://", StringComparison.Ordinal);
        string remainder = schemeSeparatorIndex >= 0
            ? original[(schemeSeparatorIndex + 3)..]
            : original;
        int queryIndex = remainder.IndexOfAny(new[] { '?', '#' });
        string endpoint = (queryIndex >= 0 ? remainder[..queryIndex] : remainder).Trim('/');

        return endpoint.StartsWith("auth", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryExtractOriginalComponent(string original, char startDelimiter, char? endDelimiter = null)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return null;
        }

        int startIndex = original.IndexOf(startDelimiter);
        if (startIndex < 0 || startIndex == original.Length - 1)
        {
            return null;
        }

        int contentStartIndex = startIndex + 1;
        int endIndex = endDelimiter is null
            ? -1
            : original.IndexOf(endDelimiter.Value, contentStartIndex);
        if (endIndex < 0)
        {
            endIndex = original.Length;
        }

        return original[contentStartIndex..endIndex];
    }

    private static bool ContainsKeyValue(string source, string key)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        foreach (string pair in source.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int valueSeparatorIndex = pair.IndexOf('=');
            string rawKey = valueSeparatorIndex >= 0 ? pair[..valueSeparatorIndex] : pair;
            string currentKey = NormalizePayloadKey(rawKey);
            if (string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizePayloadKey(string key)
    {
        string normalizedKey = key.TrimStart('?', '#', '/');
        while (normalizedKey.StartsWith("amp;", StringComparison.OrdinalIgnoreCase))
        {
            normalizedKey = normalizedKey[4..].TrimStart('?', '#', '/');
        }

        return normalizedKey;
    }

    private MainWindow GetOrCreateMainWindow()
    {
        if (_mainWindow is null)
        {
            Program.LogStartupPhase("window.construct-enter");
            try
            {
                _mainWindow = new MainWindow();
            }
            catch (Exception exception)
            {
                Program.LogStartupPhase($"window.construct-failed:{exception.GetType().FullName}:0x{exception.HResult:X8}");
                LogActivationError(exception);
                throw;
            }
            Program.LogStartupPhase("window.construct-exit");
            EnsureRuntimeMergedDictionariesLoaded();
            Program.LogStartupPhase("window.resources-ready");
            _mainWindow.ConfigureTheme(GetConfiguredTheme());
            Program.LogStartupPhase("window.theme-ready");
        }

        return _mainWindow;
    }

    internal void ApplyTheme(string? theme)
    {
        string normalizedTheme = NormalizeTheme(theme);
        _storedTheme = normalizedTheme;
        if (_services is not null)
        {
            GetService<IThemeService>().SetTheme(normalizedTheme);
        }

        if (_mainWindow is not null)
        {
            _mainWindow.ConfigureTheme(normalizedTheme);
        }
    }

    private void EnsureRuntimeMergedDictionariesLoaded()
    {
        if (_runtimeMergedDictionariesLoaded)
        {
            return;
        }

        LoadRuntimeMergedDictionaries();
        _runtimeMergedDictionariesLoaded = true;
    }

    internal T GetService<T>()
        where T : notnull
    {
        if (_services is null)
        {
            throw new InvalidOperationException("Services have not been initialized.");
        }

        return _services.GetRequiredService<T>();
    }

    internal MainWindow CurrentMainWindow => GetOrCreateMainWindow();

    private static ActivationRequest CreateActivationRequest(AppActivationArguments activationArguments)
    {
        Uri? protocolUri = null;

        if (activationArguments.Kind == ExtendedActivationKind.Protocol &&
            activationArguments.Data is IProtocolActivatedEventArgs protocolArgs)
        {
            protocolUri = protocolArgs.Uri;
        }

        if (protocolUri is null &&
            activationArguments.Kind == ExtendedActivationKind.Launch &&
            activationArguments.Data is ILaunchActivatedEventArgs launchArgs &&
            AuthLifecycleAutomationContext.TryParseProtocolArgument(launchArgs.Arguments, out Uri? automationProtocolUri))
        {
            return new ActivationRequest(ExtendedActivationKind.Protocol, automationProtocolUri);
        }

        return new ActivationRequest(activationArguments.Kind, protocolUri);
    }

    private static string FormatActivationError(Exception ex)
    {
        return UserFacingError.For(ex, UserFacingErrorKind.Activation, "activation");
    }

    private static void LogActivationError(Exception ex)
    {
        try
        {
            string logPath = Path.Combine(GetLogDirectoryPath(), "activation-error.log");
            string entry =
                $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}";
            File.AppendAllText(logPath, entry);
        }
        catch
        {
        }
    }

    private bool TryHandleLaunchPageOverride()
    {
        if (!Program.CurrentLaunchOptions.HasPageOverride)
        {
            return false;
        }

        if (string.Equals(Program.CurrentLaunchOptions.Page, "design-lab", StringComparison.OrdinalIgnoreCase))
        {
            bool hasAutomationRoots = AppDataPathPolicy.TryGetAutomationRoots(out _, out _);
            if (!DeveloperRoutePolicy.CanOpenDesignLab(
                    GetService<GlobalViewModel>().DevMode,
                    hasAutomationRoots))
            {
                return false;
            }
        }

        if (Program.CurrentLaunchOptions.IsPublicPreviewOverride)
        {
            GetService<IGitHubService>().SetAccessToken(GitHubClientService.PublicAccessToken);
            if (string.Equals(Program.CurrentLaunchOptions.Page, "profile", StringComparison.OrdinalIgnoreCase))
            {
                GetOrCreateMainWindow().ContentFrameHost.Navigate(
                    typeof(ProfilePage),
                    new UserProfilePageArgs("JitHubApp", Source: "preview"),
                    new SuppressNavigationTransitionInfo());
                return true;
            }

            GetOrCreateMainWindow().ContentFrameHost.Navigate(
                typeof(ShellPage),
                Program.CurrentLaunchOptions.Page,
                new SuppressNavigationTransitionInfo());
            return true;
        }

        Type? targetPage = Program.CurrentLaunchOptions.Page?.ToLowerInvariant() switch
        {
            "login" => typeof(LoginPage),
            "shell" => typeof(ShellPage),
            "profile" => typeof(ProfilePage),
            "settings" => typeof(SettingsPage),
            "design-lab" => typeof(DesignLabPage),
            _ => null
        };

        if (targetPage is null)
        {
            return false;
        }

        object? parameter = targetPage == typeof(ProfilePage)
            ? new UserProfilePageArgs("JitHubApp", Source: "preview")
            : targetPage == typeof(DesignLabPage) || targetPage == typeof(ShellPage)
                ? Program.CurrentLaunchOptions.Scenario
                : null;

        GetOrCreateMainWindow().ContentFrameHost.Navigate(targetPage, parameter, new SuppressNavigationTransitionInfo());
        return true;
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogUnhandledException(e.Exception, "xaml-unhandled");
    }

    private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        LogUnhandledException(e.ExceptionObject as Exception, "appdomain-unhandled");
    }

    private static bool IsLoginLaunchFailurePreview() =>
        AppDataPathPolicy.TryGetAutomationRoots(out _, out _) &&
        string.Equals(Program.CurrentLaunchOptions.Page, "login", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Program.CurrentLaunchOptions.Scenario, "login-launch-failure", StringComparison.OrdinalIgnoreCase);

    private void CurrentDomain_ProcessExit(object? sender, EventArgs e)
    {
        try
        {
            _ = ShutdownBackgroundTasksAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            _ = ShutdownDiagnosticsAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            LogUnhandledException(exception, "diagnostics-shutdown");
        }

        MarkdownRenderer.MarkdownRendererRuntime.Shutdown(TimeSpan.FromSeconds(1));
    }

    internal Task<DiagnosticsShutdownResult> ShutdownDiagnosticsAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        DiagnosticsShutdownCoordinator.DrainAsync(
            _services?.GetService<ILocalDiagnosticsStore>(),
            timeout,
            result => LogUnhandledException(
                new InvalidOperationException(result.Detail ?? result.Status.ToString()),
                "diagnostics-shutdown"),
            cancellationToken);

    internal async Task<ApplicationTaskShutdownResult> ShutdownBackgroundTasksAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        IApplicationTaskCoordinator? coordinator = _services?.GetService<IApplicationTaskCoordinator>();
        if (coordinator is null)
        {
            return new ApplicationTaskShutdownResult(true, 0);
        }

        ApplicationTaskShutdownResult result = await coordinator
            .ShutdownAsync(timeout, cancellationToken)
            .ConfigureAwait(true);
        if (!result.Completed)
        {
            RecordBackgroundTaskFailure(
                _services,
                new ApplicationTaskFailure(
                    "shutdown",
                    AccountPartition: null,
                    new TimeoutException($"{result.PendingTaskCount} background task(s) did not stop before shutdown.")));
        }

        return result;
    }

    private static void RecordBackgroundTaskFailure(
        IServiceProvider? services,
        ApplicationTaskFailure failure)
    {
        try
        {
            ISettingService? settings = services?.GetService<ISettingService>();
            if (settings is not null &&
                settings.Contains(SettingsKeys.DiagnosticsEnabled) &&
                !settings.Get<bool>(SettingsKeys.DiagnosticsEnabled))
            {
                return;
            }

            IReadOnlyDictionary<string, string> properties = TelemetrySanitizer.SanitizeProperties(
                new Dictionary<string, string?>
                {
                    ["feature"] = failure.Name,
                    ["error_kind"] = failure.Exception.GetBaseException().GetType().Name,
                    ["phase"] = "background"
                });
            _ = services?.GetService<ILocalDiagnosticsStore>()?.TryAppend(new LocalDiagnosticEvent(
                DateTimeOffset.UtcNow,
                "error",
                "background.task.failed",
                properties));
        }
        catch
        {
            // Diagnostics must never destabilize app shutdown or background refresh.
        }
    }

    internal void QueueDiagnosticsCloseProbeIfRequested()
    {
        if (!string.Equals(
                Program.CurrentLaunchOptions.Scenario,
                "diagnostics-close-probe",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ILocalDiagnosticsStore diagnostics = GetService<ILocalDiagnosticsStore>();
        int accepted = 0;
        for (int sequence = 0; sequence < DiagnosticsCloseProbeBurstCount; sequence++)
        {
            if (diagnostics.TryAppend(new LocalDiagnosticEvent(
                    DateTimeOffset.UtcNow,
                    "event",
                    DiagnosticsCloseProbeBurstName,
                    new Dictionary<string, string>
                    {
                        ["sequence"] = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["total"] = DiagnosticsCloseProbeBurstCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    })))
            {
                accepted++;
            }
        }

        bool markerAccepted = diagnostics.TryAppend(new LocalDiagnosticEvent(
            DateTimeOffset.UtcNow,
            "event",
            DiagnosticsCloseProbeMarkerName,
            new Dictionary<string, string>
            {
                ["sequence"] = DiagnosticsCloseProbeBurstCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["accepted"] = accepted.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["total"] = DiagnosticsCloseProbeBurstCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }));

        if (accepted != DiagnosticsCloseProbeBurstCount || !markerAccepted)
        {
            LogUnhandledException(
                new InvalidOperationException(
                    $"The diagnostics close probe accepted {accepted}/{DiagnosticsCloseProbeBurstCount} burst events; marker accepted: {markerAccepted}."),
                "diagnostics-close-probe");
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogUnhandledException(e.Exception, "task-unobserved");
    }

    private static void LogUnhandledException(Exception? exception, string category)
    {
        try
        {
            string logPath = Path.Combine(GetLogDirectoryPath(), $"{category}.log");
            string entry =
                $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}";
            File.AppendAllText(logPath, entry);
        }
        catch
        {
        }
    }

    internal static void LogHandledException(Exception exception, string category) =>
        LogUnhandledException(exception, category);

    private static string GetLogDirectoryPath()
    {
        string baseDirectory;
        if (AppDataPathPolicy.TryGetAutomationRoots(out string automationLocalFolder, out _))
        {
            baseDirectory = automationLocalFolder;
        }
        else
        {
            try
            {
                baseDirectory = ApplicationData.Current.LocalFolder.Path;
            }
            catch
            {
                baseDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "JitHub");
            }
        }

        string logDirectory = Path.Combine(baseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        return logDirectory;
    }

    private string GetConfiguredTheme()
    {
        if (!string.IsNullOrWhiteSpace(Program.CurrentLaunchOptions.Theme))
        {
            return NormalizeTheme(Program.CurrentLaunchOptions.Theme);
        }

        return NormalizeTheme(_storedTheme);
    }

    private static string NormalizeTheme(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme))
        {
            return ThemeConst.System;
        }

        if (string.Equals(theme, ThemeConst.Dark, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeConst.Dark;
        }

        if (string.Equals(theme, ThemeConst.Light, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeConst.Light;
        }

        return ThemeConst.System;
    }

}

