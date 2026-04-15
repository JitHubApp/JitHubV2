using System;
using JitHub.Models;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class DashboardPage : Page
{
    private readonly NavigationService _navigationService;
    private bool _initialized;
    public DashboardPageViewModel ViewModel { get; }

    public DashboardPage()
    {
        ViewModel = ((App)Application.Current).GetService<DashboardPageViewModel>();
        _navigationService = ((App)Application.Current).GetService<NavigationService>();
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ViewModel.RefreshActivityAsync();
    }

    private async void RefreshProfileButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshDashboardAsync();
    }

    private async void ReloadRepositoriesButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshRecentRepositoriesAsync();
    }

    private async void ReloadActivityButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshActivityAsync();
    }

    private async void BuyProButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.PurchaseProAsync();
    }

    private void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SignOut();
    }

    private void RecentRepositoriesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GitHubRepository repository)
        {
            _navigationService.NavigateTo(
                repository.FullName,
                typeof(RepoDetailPage),
                new RepoDetailPageArgs(RepoPageType.CodePage, repository));
        }
    }

    private async void RecentActivityList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not GitHubActivityEvent activity
            || string.IsNullOrWhiteSpace(activity.TargetUrl)
            || !Uri.TryCreate(activity.TargetUrl, UriKind.Absolute, out Uri? targetUri))
        {
            return;
        }

        await Launcher.LaunchUriAsync(targetUri);
    }
}
