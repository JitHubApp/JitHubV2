using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models;
using JitHub.Services;
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
    private string? _storedTheme;
    private bool _runtimeMergedDictionariesLoaded;
    private MainWindow? _mainWindow;
    private IServiceProvider? _services;
    private Task? _startupSessionRestoreMonitorTask;

    public App()
    {
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _storedTheme = new ThemeService(new SettingService()).GetTheme();
        ApplyStoredTheme();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Startup is centralized in Program.Main so all activation kinds share one path.
    }

    internal void HandleActivation(AppActivationArguments activationArguments)
    {
        ActivationRequest activationRequest = CreateActivationRequest(activationArguments);

        if (_dispatcherQueue.HasThreadAccess)
        {
            _ = HandleActivationAsync(activationRequest);
            return;
        }

        _ = _dispatcherQueue.TryEnqueue(() => _ = HandleActivationAsync(activationRequest));
    }

    private async Task HandleActivationAsync(ActivationRequest activationRequest)
    {
        try
        {
            await ActivateCoreAsync(activationRequest);
        }
        catch (Exception ex)
        {
            MainWindow mainWindow = GetOrCreateMainWindow();
            LogActivationError(ex);
            mainWindow.ShowActivationError(FormatActivationError(ex));
        }
    }

    private async Task ActivateCoreAsync(ActivationRequest activationRequest)
    {
        MainWindow mainWindow = GetOrCreateMainWindow();
        _services ??= BuildServices();
        InitializeNavigationTargets();
        mainWindow.ProcessActivation();
        IAuthService authService = GetService<IAuthService>();
        IAccountService accountService = GetService<IAccountService>();

        if (activationRequest.Kind != ExtendedActivationKind.Protocol && TryHandleLaunchPageOverride())
        {
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
        services.AddSingleton(new ModalService());
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IGitHubClientService, GitHubClientService>();
        services.AddSingleton<IGitHubService, GitHubService>();
        services.AddSingleton<ICommandService, CommandService>();
        services.AddSingleton<ISettingService, SettingService>();
        services.AddSingleton<IAppConfig, AppConfig>();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<EditorAssetService>();
        services.AddSingleton<GlobalViewModel>();
        services.AddTransient<DashboardPageViewModel>();
        services.AddTransient<LoginPageViewModel>();
        services.AddTransient<ProfilePageViewModel>();
        services.AddTransient<RepoSearchResultPageViewModel>();
        services.AddTransient<RepoDetailPageViewModel>();
        services.AddTransient<RepoIssuePageViewModel>();
        services.AddTransient<RepoPullRequestPageViewModel>();
        services.AddTransient<RepoManagePageViewModel>();
        services.AddTransient<RepoCommitsPageViewModel>();
        services.AddTransient<SettingsPageViewModel>();
        services.AddTransient<ShellPageViewModel>();

        IServiceProvider serviceProvider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(serviceProvider);
        return serviceProvider;
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
            if (_dispatcherQueue.HasThreadAccess)
            {
                GetOrCreateMainWindow().ShowActivationError(ex.Message);
            }
            else
            {
                _ = _dispatcherQueue.TryEnqueue(() => GetOrCreateMainWindow().ShowActivationError(ex.Message));
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
            return;
        }

        if (!authService.Authenticated && !authService.CheckAuth(accountService.GetUser()))
        {
            GetService<NavigationService>().Unauthorized();
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

        if (!string.Equals(uri.Scheme, "jithub", StringComparison.OrdinalIgnoreCase))
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
            ContainsKeyValue(query, "token") ||
            ContainsKeyValue(query, "state") ||
            ContainsKeyValue(fragment, "token") ||
            ContainsKeyValue(fragment, "state") ||
            ContainsKeyValue(query, "payload") ||
            ContainsKeyValue(fragment, "payload") ||
            ContainsKeyValue(query, "response") ||
            ContainsKeyValue(fragment, "response");

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
            _mainWindow = new MainWindow();
            EnsureRuntimeMergedDictionariesLoaded();
            _mainWindow.ConfigureTheme(GetConfiguredTheme());
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

        return new ActivationRequest(activationArguments.Kind, protocolUri);
    }

    private static string FormatActivationError(Exception ex)
    {
        return string.IsNullOrWhiteSpace(ex.Message)
            ? ex.GetType().Name
            : ex.Message;
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

        Type? targetPage = Program.CurrentLaunchOptions.Page?.ToLowerInvariant() switch
        {
            "login" => typeof(LoginPage),
            "shell" => typeof(ShellPage),
            "settings" => typeof(SettingsPage),
            "design-lab" => typeof(DesignLabPage),
            _ => null
        };

        if (targetPage is null)
        {
            return false;
        }

        object? parameter = targetPage == typeof(DesignLabPage) || targetPage == typeof(ShellPage)
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

    private static string GetLogDirectoryPath()
    {
        string baseDirectory;
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

