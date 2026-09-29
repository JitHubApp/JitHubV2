using System.ComponentModel;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using JitHub.Services.Markdown;
using JitHub.WinUI.Views.Controls.CodeViewer;
using MarkdownRenderer.Images;

namespace JitHub.WinUI.Views.Controls.CodeViewer.Renderers;

/// <summary>
/// Renders Markdown files with a rich/plain toggle.
/// DataContext must be a <see cref="RepoFilePreviewViewModel"/>.
/// </summary>
public sealed partial class MarkdownPreview : UserControl
{
    private RepoFilePreviewViewModel? _subscribedViewModel;
    private CodeEditorControl? _plainPanel;

    public MarkdownPreview()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private RepoFilePreviewViewModel? ViewModel => DataContext as RepoFilePreviewViewModel;

    private string? CurrentBaseUrl => ViewModel?.GitHubBlobUrl;

    private string? CurrentDocumentPath => ViewModel?.CurrentFile?.Path;

    private MarkdownDocumentSource? CurrentDocumentSource =>
        MarkdownDocumentSourceFactory.TryCreateRepositoryFile(
            ViewModel?.CurrentFile?.Sha ?? ViewModel?.CurrentFile?.Path ?? "unknown",
            CurrentBaseUrl,
            CurrentDocumentPath);

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        SubscribeToViewModel(DataContext as RepoFilePreviewViewModel);
        SyncSegmented();
        SyncPanels();
        Bindings.Update();
    }

    private void SubscribeToViewModel(RepoFilePreviewViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribedViewModel, viewModel))
        {
            return;
        }

        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _subscribedViewModel = viewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(RepoFilePreviewViewModel.ShowRichPreview))
        {
            SyncSegmented();
            SyncPanels();
            return;
        }

        if (args.PropertyName == nameof(RepoFilePreviewViewModel.Text) && _plainPanel is not null)
        {
            _plainPanel.Text = ViewModel?.Text ?? string.Empty;
        }

        if (args.PropertyName is nameof(RepoFilePreviewViewModel.CurrentFile)
            or nameof(RepoFilePreviewViewModel.GitHubBlobUrl))
        {
            Bindings.Update();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        SubscribeToViewModel(DataContext as RepoFilePreviewViewModel);

    private void OnUnloaded(object sender, RoutedEventArgs e) => SubscribeToViewModel(null);

    private void SyncSegmented()
    {
        ViewModeSegmented.SelectedIndex = (ViewModel?.ShowRichPreview ?? true) ? 0 : 1;
    }

    private void SyncPanels()
    {
        bool rich = ViewModel?.ShowRichPreview ?? true;
        RichPanel.Visibility = rich ? Visibility.Visible : Visibility.Collapsed;
        if (!rich)
        {
            EnsurePlainPanel();
        }

        PlainPanelHost.Visibility = rich ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EnsurePlainPanel()
    {
        if (_plainPanel is not null)
        {
            _plainPanel.Text = ViewModel?.Text ?? string.Empty;
            return;
        }

        _plainPanel = new CodeEditorControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsReadOnlyEditor = true,
            LanguageId = "markdown",
            Text = ViewModel?.Text ?? string.Empty,
        };
        PlainPanelHost.Children.Add(_plainPanel);
    }

    private void ViewModeSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null)
        {
            return;
        }

        bool wantsRich = ViewModeSegmented.SelectedIndex == 0;
        if (vm.ShowRichPreview != wantsRich)
        {
            vm.ShowRichPreview = wantsRich;
        }

        SyncPanels();
    }
}
