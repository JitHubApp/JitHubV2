using JitHub.Services;
using JitHub.WinUI.ViewModels;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class ShellPage : Page
{
    private CancellationTokenSource? _notificationLifetime;
    public ShellViewModel ViewModel { get; } = new();

    public ShellPage()
    {
        this.InitializeComponent();
        DataContext = ViewModel;
        ViewModel.LoadApplication(new RelayCommand(OpenModal), new RelayCommand(CloseModal));
        ViewModel.InitializeDesktopIntegration(((App)Application.Current).CurrentMainWindow);
        var notificationService = ((App)Application.Current).GetService<INotificationService>();
        notificationService.Register(new RelayCommand<string?>(PushNotification));
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        MainWindow mainWindow = ((App)Application.Current).CurrentMainWindow;
        mainWindow.SetPageTitleBar(TitleBarHost);
        QueueTitleBarPassthroughUpdate(mainWindow);
        ConnectedAnimation animation = ConnectedAnimationService.GetForCurrentView()
            .GetAnimation("AppLogoAnimation");
        if (animation != null)
        {
            animation.TryStart(AppLogoShellPage);
        }
        ViewModel.RegisterSearchDebounce(SearchBox);
        _ = ViewModel.OnNavigatedTo();
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        ((App)Application.Current).CurrentMainWindow.ClearTitleBarPassthroughRegions();
        ConnectedAnimationService.GetForCurrentView()
            .PrepareToAnimate("AppLogoLogoutAnimation", AppLogoShellPage);
    }

    private void OpenModal()
    {
        Modal.Visibility = Visibility.Visible;
        SearchBox.IsEnabled = false;
    }

    private void CloseModal()
    {
        Modal.Visibility = Visibility.Collapsed;
        SearchBox.IsEnabled = true;
    }

    private void PushNotification(string? message)
    {
        _notificationLifetime?.Cancel();
        _notificationLifetime?.Dispose();

        CancellationTokenSource lifetime = new();
        _notificationLifetime = lifetime;

        NotificationBar.Message = message ?? string.Empty;
        NotificationBar.IsOpen = true;

        _ = CloseNotificationAsync(lifetime.Token);
    }

    private void TabView_AddTabButtonClick(TabView sender, object args)
    {
        ViewModel.OnAddTab(sender, args);
    }

    private void TabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        ViewModel.OnTabClose(sender, args);
    }

    private void TabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.OnTabSelectionChanged(sender, e);
    }

    private void Page_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 768)
        {
            VisualStateManager.GoToState(this, "WideLayout", false);
        }
        else
        {
            VisualStateManager.GoToState(this, "NarrowLayout", false);
        }

        QueueTitleBarPassthroughUpdate(((App)Application.Current).CurrentMainWindow);
    }

    private async Task CloseNotificationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        NotificationBar.IsOpen = false;
    }

    private void QueueTitleBarPassthroughUpdate(MainWindow mainWindow)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            mainWindow.SetTitleBarPassthroughRegions(SearchBoxContainer, SearchBox, ShellMenuButton);
        });
    }

}


