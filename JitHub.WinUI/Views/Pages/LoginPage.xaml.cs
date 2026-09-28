using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Pages;
using Microsoft.UI.Xaml;
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
            _navigationService.GoHome();
            return;
        }

        ViewModel.PrepareForDisplay();
        StopOfflineSessionRecovery();
        _offlineRecoveryLifetime = new CancellationTokenSource();
        CancellationToken token = _offlineRecoveryLifetime.Token;
        UiTaskGuard.Run(() => ObserveStoredSessionAsync(token), "ui-login-offline-recovery");
    }

    private async Task ObserveStoredSessionAsync(CancellationToken cancellationToken)
    {
        await ViewModel.WaitForStoredSessionAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (ViewModel.IsAuthenticated)
        {
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
                _navigationService.GoHome();
                return;
            }
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => StopOfflineSessionRecovery();

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
                _navigationService.GoHome();
            }
        }, "ui-login-page");
    }
}
