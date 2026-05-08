using System;
using System.ComponentModel;
using System.Threading;
using JitHub.Models.NavArgs;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class RepoCodePage : Page
{
    private readonly App _app = (App)Application.Current;
    private CancellationTokenSource? _initCts;

    public RepoCodePageViewModel ViewModel { get; }

    public RepoCodePage()
    {
        ViewModel = _app.GetService<RepoCodePageViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not CodeViewerNavArg arg || arg.Repo is null)
        {
            ShowError("Repository context is required to open the code viewer.");
            return;
        }

        var repo = arg.Repo;
        var owner = repo.Owner?.Login;
        var name = repo.Name;
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
        {
            ShowError("Repository metadata is incomplete.");
            return;
        }

        string? gitRef = arg.IsBranch ? arg.Branch : arg.GitRef;
        if (string.IsNullOrWhiteSpace(gitRef))
        {
            gitRef = repo.DefaultBranch;
        }

        if (string.IsNullOrWhiteSpace(gitRef))
        {
            ShowError("Could not determine which branch to load.");
            return;
        }

        _initCts?.Cancel();
        _initCts?.Dispose();
        _initCts = new CancellationTokenSource();

        try
        {
            await ViewModel.InitializeAsync(owner!, name!, gitRef!, _initCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _initCts?.Cancel();
        _initCts?.Dispose();
        _initCts = null;
        base.OnNavigatedFrom(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RepoCodePageViewModel.LoadError))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!string.IsNullOrEmpty(ViewModel.LoadError))
                {
                    ShowError(ViewModel.LoadError!);
                }
                else
                {
                    ErrorBanner.Visibility = Visibility.Collapsed;
                }
            });
        }
    }

    private void ShowError(string message)
    {
        ErrorMessageText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }
}
