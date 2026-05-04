using System;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        try
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            await ViewModel.RefreshActivityAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load dashboard: {ex}");
        }
    }

    private async void RefreshProfileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RefreshDashboardAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to refresh dashboard profile: {ex}");
        }
    }

    private async void ReloadRepositoriesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RefreshRecentRepositoriesAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to reload dashboard repositories: {ex}");
        }
    }

    private async void ReloadActivityButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RefreshActivityAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to reload dashboard activity: {ex}");
        }
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
}
