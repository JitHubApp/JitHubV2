using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.CodeViewer.Renderers;

/// <summary>
/// Renders bounded CSV and TSV data as a virtualized table or read-only source text.
/// </summary>
public sealed partial class CsvPreview : UserControl
{
    private CancellationTokenSource? _parseCancellation;
    private RepoFilePreviewViewModel? _subscribedViewModel;
    private int _parseGeneration;

    public CsvPreview()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private RepoFilePreviewViewModel? ViewModel => DataContext as RepoFilePreviewViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToViewModel(ViewModel);
        SyncSegmented();
        UpdateContent();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SubscribeToViewModel(null);
        CancelParse();
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        SubscribeToViewModel(IsLoaded ? ViewModel : null);
        SyncSegmented();
        UpdateContent();
    }

    private void SubscribeToViewModel(RepoFilePreviewViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribedViewModel, viewModel))
        {
            return;
        }

        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        _subscribedViewModel = viewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RepoFilePreviewViewModel.ShowRichPreview))
        {
            SyncSegmented();
            UpdateContent();
        }
        else if (e.PropertyName is nameof(RepoFilePreviewViewModel.Text) or
                 nameof(RepoFilePreviewViewModel.CurrentFile))
        {
            UpdateContent();
        }
    }

    private void SyncSegmented()
    {
        ViewModeSegmented.SelectedIndex = (ViewModel?.ShowRichPreview ?? true) ? 0 : 1;
    }

    private void ViewModeSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RepoFilePreviewViewModel? viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        bool wantsRich = ViewModeSegmented.SelectedIndex == 0;
        if (viewModel.ShowRichPreview != wantsRich)
        {
            viewModel.ShowRichPreview = wantsRich;
        }
        else
        {
            UpdateContent();
        }
    }

    private void UpdateContent()
    {
        CancelParse();

        RepoFilePreviewViewModel? viewModel = ViewModel;
        string text = viewModel?.Text ?? string.Empty;
        bool rich = viewModel?.ShowRichPreview ?? true;
        DataTable.Visibility = rich ? Visibility.Visible : Visibility.Collapsed;
        PlainEditor.Visibility = rich ? Visibility.Collapsed : Visibility.Visible;
        PlainEditor.Text = text;

        if (!rich || !IsLoaded)
        {
            return;
        }

        char delimiter = viewModel?.CurrentFile?.Path.EndsWith(
            ".tsv",
            StringComparison.OrdinalIgnoreCase) == true
            ? '\t'
            : ',';

        DataTable.ShowStatus(L(
            "RepoCode/Csv/Loading",
            "Preparing table..."));

        CancellationTokenSource cancellation = new();
        _parseCancellation = cancellation;
        int generation = ++_parseGeneration;
        _ = ParseAndPresentAsync(text, delimiter, generation, cancellation.Token);
    }

    private async Task ParseAndPresentAsync(
        string text,
        char delimiter,
        int generation,
        CancellationToken cancellationToken)
    {
        CsvParseResult result = await CsvDocumentParser.ParseAsync(
            text,
            delimiter,
            cancellationToken);
        if (result.WasCanceled ||
            cancellationToken.IsCancellationRequested ||
            generation != _parseGeneration ||
            !IsLoaded ||
            ViewModel?.ShowRichPreview != true)
        {
            return;
        }

        if (result.Succeeded)
        {
            DataTable.SetDocument(result.Document!);
        }
        else
        {
            DataTable.ShowStatus(GetFailureMessage(result.Failure));
        }
    }

    private void CancelParse()
    {
        _parseGeneration++;
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _parseCancellation, null);
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private static string GetFailureMessage(CsvParseFailure failure) => failure switch
    {
        CsvParseFailure.InputTooLarge => LF(
            "RepoCode/Csv/InputTooLarge",
            "Rich preview supports files up to {0} KiB. Plain view is still available.",
            CsvDocumentParser.MaximumInputCharacters / 1024),
        CsvParseFailure.TooManyColumns => LF(
            "RepoCode/Csv/TooManyColumns",
            "Rich preview supports up to {0} columns. Plain view is still available.",
            CsvDocumentParser.MaximumColumns),
        CsvParseFailure.TooManyRows => LF(
            "RepoCode/Csv/TooManyRows",
            "Rich preview supports up to {0:N0} rows. Plain view is still available.",
            CsvDocumentParser.MaximumDataRows),
        CsvParseFailure.UnterminatedQuotedField => L(
            "RepoCode/Csv/UnterminatedQuotedField",
            "A quoted field is not closed. Check the file in plain view."),
        CsvParseFailure.InvalidQuote => L(
            "RepoCode/Csv/InvalidQuote",
            "A quote appears outside a valid quoted field. Check the file in plain view."),
        _ => L(
            "RepoCode/Csv/Empty",
            "No rows to display."),
    };

    private static string L(string key, string fallback) =>
        LocalizedResourceText.GetString(key, fallback);

    private static string LF(string key, string fallback, params object?[] arguments) =>
        LocalizedResourceText.Format(key, fallback, arguments);
}
