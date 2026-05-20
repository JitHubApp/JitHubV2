using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.WinUI.ViewModels.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class RepoManagePage : Page
{
    private bool _initialized;

    public RepoManagePageViewModel ViewModel { get; }

    public RepoManagePage()
    {
        ViewModel = ((App)Application.Current).GetService<RepoManagePageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await LoadRepositoriesSafelyAsync();
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadRepositoriesSafelyAsync();
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearSelection();
    }

    private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<GitHubRepositorySelectionItem> selectedRepositories = ViewModel.GetSelectedRepositories();
            if (selectedRepositories.Count == 0)
            {
                return;
            }

            ContentDialogResult dialogResult = await ShowDeleteConfirmationAsync(selectedRepositories.Count);
            if (dialogResult != ContentDialogResult.Primary)
            {
                return;
            }

            RepositoryDeletionResult? deletionResult = await ViewModel.DeleteSelectedAsync(selectedRepositories);
            if (deletionResult?.HasFailures == true)
            {
                await ShowDeleteFailuresAsync(deletionResult.Failures);
            }
        }
        catch (Exception ex)
        {
            ViewModel.ShowUnexpectedError(ex);
        }
    }

    private async Task LoadRepositoriesSafelyAsync()
    {
        try
        {
            await ViewModel.LoadRepositoriesAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ShowUnexpectedError(ex);
        }
    }

    private async Task<ContentDialogResult> ShowDeleteConfirmationAsync(int selectedRepositoryCount)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.DeleteDialogTitle,
            Content = new TextBlock
            {
                Text = ViewModel.FormatDeleteDialogContent(selectedRepositoryCount),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = ViewModel.DeleteDialogConfirmButtonText,
            CloseButtonText = ViewModel.DeleteDialogCloseButtonText,
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync();
    }

    private async Task ShowDeleteFailuresAsync(IReadOnlyList<string> failures)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.DeleteFailureDialogTitle,
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock
                {
                    Text = string.Join(Environment.NewLine, failures),
                    TextWrapping = TextWrapping.Wrap
                }
            },
            CloseButtonText = ViewModel.CloseButtonText,
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }
}
