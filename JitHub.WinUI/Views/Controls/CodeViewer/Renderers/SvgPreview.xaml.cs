using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.Services.Markdown;
using JitHub.WinUI.ViewModels.CodeViewer;
using JitHub.WinUI.Views.Controls.App;
using MarkdownRenderer.Images;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Views.Controls.CodeViewer.Renderers;

/// <summary>
/// Renders validated repository SVG files through the app-owned tiled bitmap viewport.
/// </summary>
public sealed partial class SvgPreview : UserControl
{
    private static readonly TimeSpan ParseDeadline = TimeSpan.FromSeconds(3);

    private readonly DispatcherQueue _dispatcher;
    private readonly IMarkdownSvgRenderer _svgRenderer;
    private readonly IRepositorySvgRasterizer _rasterizer;
    private readonly SvgPreviewRequestGate _requestGate = new();
    private RepoFilePreviewViewModel? _viewModel;
    private bool _isAttached;
    private bool _documentUsesThemeInputs = true;
    private bool _documentHasText;

    public event Action<string, string>? ActionCompleted;

    public SvgPreview()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _svgRenderer = JitHubMarkdownRuntime.SvgRenderer;
        _rasterizer = new RepositorySvgRasterizer(_svgRenderer);
        SvgViewport.RenderFailed += SvgViewport_RenderFailed;
        SvgViewport.ZoomSettled += SvgViewport_ZoomSettled;
        DataContextChanged += OnDataContextChanged;
        ActualThemeChanged += OnActualThemeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        SubscribeToViewModel(_isAttached ? DataContext as RepoFilePreviewViewModel : null);
        QueueLoad();
    }

    private void SubscribeToViewModel(RepoFilePreviewViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(RepoFilePreviewViewModel.Bytes))
        {
            _dispatcher.TryEnqueue(() => QueueLoad());
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_documentUsesThemeInputs)
        {
            QueueLoad(retainCurrentBitmap: true);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _isAttached = true;
        _svgRenderer.CacheInvalidated += OnSvgCacheInvalidated;
        SubscribeToViewModel(DataContext as RepoFilePreviewViewModel);
        SvgViewport.AttachScrollHost(SvgScrollViewer);
        QueueLoad();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _svgRenderer.CacheInvalidated -= OnSvgCacheInvalidated;
        _isAttached = false;
        _documentHasText = false;
        SubscribeToViewModel(null);
        _requestGate.CancelCurrent();
        SvgViewport.Clear();
    }

    private void OnSvgCacheInvalidated(object? sender, EventArgs args) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (_isAttached && _documentHasText)
            {
                QueueLoad(retainCurrentBitmap: true);
            }
        });

    private void QueueLoad(bool retainCurrentBitmap = false)
    {
        if (!_isAttached)
        {
            return;
        }

        byte[]? bytes = _viewModel?.Bytes;
        SvgPreviewRequest request = _requestGate.Begin();
        _documentUsesThemeInputs = true;
        if (!retainCurrentBitmap)
        {
            _documentHasText = false;
            SvgViewport.Clear();
            SvgViewport.Visibility = Visibility.Collapsed;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        AutomationProperties.SetItemStatus(ErrorText, string.Empty);
        AutomationProperties.SetHelpText(ErrorText, string.Empty);
        UiTaskGuard.Observe(
            LoadSvgAsync(bytes, request, retainCurrentBitmap),
            "ui-svg-preview");
    }

    private async Task LoadSvgAsync(
        byte[]? bytes,
        SvgPreviewRequest request,
        bool retainCurrentBitmap)
    {
        RepositorySvgDocument? document = null;
        try
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(
                request.CancellationToken);
            deadline.CancelAfter(ParseDeadline);
            document = await _rasterizer.LoadAsync(
                bytes,
                CultureInfo.CurrentUICulture.Name,
                ActualTheme == ElementTheme.Dark
                    ? MarkdownSvgColorScheme.Dark
                    : MarkdownSvgColorScheme.Light,
                ResolveSemanticColor(),
                deadline.Token).ConfigureAwait(false);

            RepositorySvgDocument? published = document;
            await RunOnUiAsync(() =>
            {
                if (!_isAttached || !_requestGate.IsCurrent(request) || published is null)
                {
                    return;
                }

                if (published.Info.HasText &&
                    published.CacheGeneration != _svgRenderer.CacheGeneration)
                {
                    QueueLoad(retainCurrentBitmap: true);
                    return;
                }

                SvgViewport.SetDocument(published, _rasterizer, retainCurrentBitmap);
                _documentUsesThemeInputs = published.Info.UsesColorScheme || published.Info.UsesCurrentColor;
                _documentHasText = published.Info.HasText;
                document = null;
                ErrorText.Visibility = Visibility.Collapsed;
                SvgViewport.Visibility = Visibility.Visible;
            }).ConfigureAwait(false);

            if (published is null)
            {
                await RunOnUiAsync(() =>
                {
                    if (_requestGate.IsCurrent(request))
                    {
                        ShowUnavailable();
                        MarkdownSvgException exception = new(
                            MarkdownSvgFailureReason.UnsupportedContent);
                        AutomationProperties.SetItemStatus(
                            ErrorText,
                            $"svg-unavailable:{exception.Reason}");
                        AutomationProperties.SetHelpText(ErrorText, exception.Message);
                    }
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            await ShowUnavailableIfCurrentAsync(
                request,
                new MarkdownSvgException(MarkdownSvgFailureReason.Timeout)).ConfigureAwait(false);
        }
        catch (MarkdownSvgException exception) when (
            exception.Reason == MarkdownSvgFailureReason.Canceled &&
            request.CancellationToken.IsCancellationRequested)
        {
        }
        catch (MarkdownSvgException exception) when (
            exception.Reason == MarkdownSvgFailureReason.Canceled)
        {
            await ShowUnavailableIfCurrentAsync(
                request,
                new MarkdownSvgException(
                    MarkdownSvgFailureReason.Timeout,
                    innerException: exception)).ConfigureAwait(false);
        }
        catch (MarkdownSvgException exception)
        {
            await ShowUnavailableIfCurrentAsync(request, exception).ConfigureAwait(false);
        }
        catch
        {
            await ShowUnavailableIfCurrentAsync(request).ConfigureAwait(false);
        }
        finally
        {
            document?.Dispose();
            _requestGate.Complete(request);
        }
    }

    private Task ShowUnavailableIfCurrentAsync(
        SvgPreviewRequest request,
        MarkdownSvgException? exception = null) => RunOnUiAsync(() =>
    {
        if (_requestGate.IsCurrent(request))
        {
            ShowUnavailable();
            if (exception is not null)
            {
                AutomationProperties.SetItemStatus(ErrorText, $"svg-unavailable:{exception.Reason}");
                AutomationProperties.SetHelpText(ErrorText, exception.Message);
            }
        }
    });

    private void SvgViewport_RenderFailed(object? sender, AppSvgRenderFailedEventArgs e)
    {
        if (_isAttached)
        {
            ShowUnavailable();
            string status = e.Exception is MarkdownSvgException svgException
                ? $"svg-unavailable:{svgException.Reason}"
                : $"render-failed:{e.Exception.GetType().Name}:0x{e.Exception.HResult:x8}";
            AutomationProperties.SetItemStatus(ErrorText, status);
            AutomationProperties.SetHelpText(ErrorText, e.Exception.Message);
        }
    }

    private void SvgViewport_ZoomSettled(object? sender, EventArgs e) =>
        ActionCompleted?.Invoke(
            RepoCodeTelemetryActions.SvgZoom,
            TelemetryTaxonomy.Results.Success);

    private void ShowUnavailable()
    {
        _documentHasText = false;
        SvgViewport.Clear();
        ErrorText.Visibility = Visibility.Visible;
        SvgViewport.Visibility = Visibility.Collapsed;
    }

    private static MarkdownSvgColor? ResolveSemanticColor()
    {
        if (Application.Current?.Resources is not { } resources ||
            !resources.TryGetValue("AppInkBrush", out object? value) ||
            value is not SolidColorBrush brush)
        {
            return null;
        }

        Windows.UI.Color color = brush.Color;
        return new MarkdownSvgColor(color.R, color.G, color.B, color.A);
    }

    private Task RunOnUiAsync(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }))
        {
            completion.SetResult();
        }

        return completion.Task;
    }
}
