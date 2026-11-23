using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Core.Services.Authentication;
using JitHub.Core.Services.Storage;
using JitHub.Core.ViewModels.Authentication;
using JitHub.WinUI.Services.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class LoginPage : Page
{
    private static TimeSpan OfflineRetryInterval =>
        (Program.CurrentLaunchOptions.Scenario is
            AuthLifecycleScenario.ExpiredOfflineRecovery or AuthLifecycleScenario.OfflineLaunch) &&
        AppDataPathPolicy.TryGetAutomationRoots(out _, out _)
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromSeconds(30);
    private readonly NavigationService _navigationService;
    private CancellationTokenSource? _offlineRecoveryLifetime;
    public LoginPageViewModel ViewModel { get; }

    public LoginPage()
    {
        ViewModel = ((App)Application.Current).GetService<LoginPageViewModel>();
        _navigationService = ((App)Application.Current).GetService<NavigationService>();
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsAuthenticated)
        {
            ((App)Application.Current).CurrentMainWindow.ClearStatus();
            _navigationService.GoHome();
            return;
        }

        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.PrepareForDisplay();
        ViewModel.BeginObservingSessionActivity();
        StartOfflineSessionRecovery();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoginPageViewModel.StatusText))
        {
            FrameworkElementAutomationPeer
                .CreatePeerForElement(LoginStatusTextBlock)?
                .RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private void StartOfflineSessionRecovery()
    {
        StopOfflineSessionRecovery();
        _offlineRecoveryLifetime = new CancellationTokenSource();
        CancellationToken token = _offlineRecoveryLifetime.Token;
        UiTaskGuard.Run(() => ObserveStoredSessionAsync(token), "ui-login-offline-recovery");
    }

    private async Task ObserveStoredSessionAsync(CancellationToken cancellationToken)
    {
        Task restore = ViewModel.WaitForStoredSessionAsync();
        if (!restore.IsCompleted)
        {
            Task first = await Task.WhenAny(
                restore,
                Task.Delay(LoginPageViewModel.SessionRestoreActionDelay, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(first, restore))
            {
                ViewModel.ShowSlowSessionRestore();
            }
        }

        await restore;
        cancellationToken.ThrowIfCancellationRequested();
        if (ViewModel.IsAuthenticated)
        {
            ((App)Application.Current).CurrentMainWindow.ClearStatus();
            _navigationService.GoHome();
            return;
        }

        ViewModel.ShowSavedSessionRecoveryStatus();
        while (ViewModel.IsSavedSessionRecoveryPending)
        {
            await Task.Delay(OfflineRetryInterval, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (await ViewModel.RetrySavedSessionAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ((App)Application.Current).CurrentMainWindow.ClearStatus();
                _navigationService.GoHome();
                return;
            }
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopOfflineSessionRecovery();
        ViewModel.EndObservingSessionActivity();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void StopOfflineSessionRecovery()
    {
        CancellationTokenSource? lifetime = _offlineRecoveryLifetime;
        _offlineRecoveryLifetime = null;
        lifetime?.Cancel();
        lifetime?.Dispose();
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        StopOfflineSessionRecovery();
        UiTaskGuard.Run(async () =>
        {
            await ViewModel.StartLoginAsync();
            if (ViewModel.IsAuthenticated)
            {
                ((App)Application.Current).CurrentMainWindow.ClearStatus();
                _navigationService.GoHome();
            }
            else if (IsLoaded && ViewModel.IsSavedSessionRecoveryPending)
            {
                StartOfflineSessionRecovery();
            }
        }, "ui-login-page");
    }
}
