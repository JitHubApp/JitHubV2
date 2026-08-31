using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.CodeViewer;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace JitHub.WinUI.Views.Controls.CodeViewer.Renderers;

public sealed partial class CodePreview : UserControl
{
    private CancellationTokenSource? _outlineCts;
    private RepoFilePreviewViewModel? _subscribedViewModel;
    private long _bindingUpdateGeneration;

    public event Action<string>? ActionExecuted;
    public event Action<string, string>? ActionCompleted;

    public CodePreview()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private RepoFilePreviewViewModel? ViewModel => DataContext as RepoFilePreviewViewModel;

    public bool FocusEditor() => Editor.FocusEditor();

    public void OpenFind()
    {
        bool wasClosed = FindPanel.Visibility != Visibility.Visible;
        FindPanel.Visibility = Visibility.Visible;
        FindTextBox.Focus(FocusState.Programmatic);
        FindTextBox.SelectAll();
        if (wasClosed)
        {
            ActionExecuted?.Invoke(RepoCodeTelemetryActions.Find);
        }
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _subscribedViewModel = ViewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        ApplyPreviewBindings();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedViewModel is null && ViewModel is { } viewModel)
        {
            _subscribedViewModel = viewModel;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ApplyPreviewBindings();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _bindingUpdateGeneration);
        RetireOutlineRequest();
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RepoFilePreviewViewModel.CurrentFile))
        {
            Interlocked.Increment(ref _bindingUpdateGeneration);
            ApplyPreviewBindings();
        }
        else if (e.PropertyName == nameof(RepoFilePreviewViewModel.Text))
        {
            Interlocked.Increment(ref _bindingUpdateGeneration);
            ApplyPreviewBindings();
        }
        else if (e.PropertyName == nameof(RepoFilePreviewViewModel.LanguageId))
        {
            QueuePreviewBindingFallback();
        }
        else if (e.PropertyName == nameof(RepoFilePreviewViewModel.IsLoading))
        {
            PublishEditorReadiness();
        }
    }

    private void QueuePreviewBindingFallback()
    {
        long generation = Interlocked.Increment(ref _bindingUpdateGeneration);
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (generation == Volatile.Read(ref _bindingUpdateGeneration))
                {
                    ApplyPreviewBindings();
                }
            });
    }

    private void ApplyPreviewBindings()
    {
        Bindings.Update();
        PublishEditorReadiness();
        QueueOutlineBuild();
    }

    private void PublishEditorReadiness()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.IsLoading)
        {
            Editor.MarkContentLoading();
        }
        else if (viewModel.Text is not null)
        {
            Editor.MarkContentReadyIfApplied(viewModel.Text);
        }
    }

    private void QueueOutlineBuild()
    {
        RetireOutlineRequest();
        CancellationTokenSource cts = new();
        _outlineCts = cts;
        CancellationToken cancellationToken = cts.Token;
        string? text = ViewModel?.Text;
        string? language = ViewModel?.LanguageId;
        UiTaskGuard.Observe(
            BuildOutlineObservedAsync(text, language, cts),
            "ui-code-preview",
            _ => ShowOutlineFailure(cancellationToken));
    }

    private void ShowOutlineFailure(CancellationToken cancellationToken)
    {
        if (!IsLoaded || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        SymbolsList.ItemsSource = null;
        NoSymbolsText.Text = LocalizedResourceText.GetString(
            "RepoCode/Outline/Error",
            "Outline is temporarily unavailable for this file.");
        NoSymbolsText.Visibility = Visibility.Visible;
        SymbolsList.Visibility = Visibility.Collapsed;
        ActionCompleted?.Invoke(
            RepoCodeTelemetryActions.Outline,
            TelemetryTaxonomy.Results.Error);
    }

    private async Task BuildOutlineObservedAsync(
        string? text,
        string? language,
        CancellationTokenSource request)
    {
        try
        {
            CancellationToken token = request.Token;
            IReadOnlyList<CodeSymbol> symbols = await Task.Run(
                () => CodeSymbolExtractor.Extract(language, text),
                token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            TaskCompletionSource<bool> committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (!ReferenceEquals(_outlineCts, request) || token.IsCancellationRequested) return;
                    SymbolsList.ItemsSource = symbols;
                    NoSymbolsText.Text = CodeSymbolExtractor.Supports(language)
                        ? LocalizedResourceText.GetString("RepoCode/Outline/NoSymbols", "No symbols were found in this file.")
                        : LocalizedResourceText.GetString("RepoCode/Outline/Unavailable", "Outline is not available for this language.");
                    NoSymbolsText.Visibility = symbols.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    SymbolsList.Visibility = symbols.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                }
                finally
                {
                    committed.TrySetResult(true);
                }
            }))
            {
                return;
            }

            await committed.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _outlineCts, null, request);
            request.Dispose();
        }
    }

    private void RetireOutlineRequest()
    {
        CancellationTokenSource? previous = Interlocked.Exchange(ref _outlineCts, null);
        if (previous is null) return;
        previous.Cancel();
    }

    private void Editor_FindRequested(object? sender, EventArgs e) => OpenFind();

    private void FindButton_Click(object sender, RoutedEventArgs e) => OpenFind();

    private void CloseFindButton_Click(object sender, RoutedEventArgs e)
        => CloseFind();

    private void CloseFind()
    {
        FindPanel.Visibility = Visibility.Collapsed;
        ClearFindStatus();
        Editor.FocusEditor();
    }

    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (FindPanel.Visibility != Visibility.Visible || string.IsNullOrEmpty(FindTextBox.Text))
        {
            ClearFindStatus();
            return;
        }

        FindAndReport(reverse: false);
    }

    private void FindTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            FindAndReport(reverse: false);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseFind();
        }
    }

    private void PreviousMatchButton_Click(object sender, RoutedEventArgs e) => FindAndReport(reverse: true);

    private void NextMatchButton_Click(object sender, RoutedEventArgs e) => FindAndReport(reverse: false);

    private void SymbolsButton_Click(object sender, RoutedEventArgs e) =>
        ActionExecuted?.Invoke(RepoCodeTelemetryActions.Outline);

    private void FindAndReport(bool reverse)
    {
        bool found = Editor.FindNext(FindTextBox.Text, reverse);
        FindStatus.Text = found
            ? LocalizedResourceText.Format("RepoCode/Find/Line", "Line {0}", Editor.CurrentLine)
            : LocalizedResourceText.GetString("RepoCode/Find/NoMatches", "No matches");
        AutomationProperties.SetName(FindStatus, FindStatus.Text);
        AutomationProperties.SetItemStatus(FindStatus, found ? "match" : "no-match");
    }

    private void ClearFindStatus()
    {
        FindStatus.Text = string.Empty;
        AutomationProperties.SetName(FindStatus, string.Empty);
        AutomationProperties.SetItemStatus(FindStatus, string.Empty);
    }

    private void SymbolsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CodeSymbol symbol)
        {
            SymbolsButton.Flyout?.Hide();
            Editor.GoToLine(symbol.LineNumber);
            Editor.FocusEditor();
        }
    }

    private void SymbolsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not null && args.Item is CodeSymbol symbol)
        {
            AutomationProperties.SetAutomationId(args.ItemContainer, symbol.AutomationId);
            AutomationProperties.SetName(args.ItemContainer, symbol.AutomationName);
        }
    }

    private void CopyLineLinkButton_Click(object sender, RoutedEventArgs e)
    {
        string? url = ViewModel?.GitHubBlobUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            ActionCompleted?.Invoke(
                RepoCodeTelemetryActions.CopyLineLink,
                TelemetryTaxonomy.Results.Error);
            return;
        }

        bool succeeded = PlatformHelper.CopyString(
            GitHubCodeUrlBuilder.AppendLineFragment(url, Editor.CurrentLine));
        ActionCompleted?.Invoke(
            RepoCodeTelemetryActions.CopyLineLink,
            succeeded ? TelemetryTaxonomy.Results.Success : TelemetryTaxonomy.Results.Error);
    }
}
