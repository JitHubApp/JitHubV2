using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.ViewModels.CodeViewer;
using JitHub.WinUI.Views.Controls.App;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.CodeViewer.Renderers;

/// <summary>
/// Renders validated repository SVG files through the app-owned tiled bitmap viewport.
/// </summary>
public sealed partial class SvgPreview : UserControl
{
    private static readonly TimeSpan ParseDeadline = TimeSpan.FromSeconds(2);

    private readonly DispatcherQueue _dispatcher;
    private readonly IRepositorySvgRasterizer _rasterizer = new RepositorySvgRasterizer();
    private readonly SvgPreviewRequestGate _requestGate = new();
    private RepoFilePreviewViewModel? _viewModel;
    private bool _isAttached;

    public SvgPreview()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        SvgViewport.RenderFailed += SvgViewport_RenderFailed;
        DataContextChanged += OnDataContextChanged;
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
            _dispatcher.TryEnqueue(QueueLoad);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _isAttached = true;
        SubscribeToViewModel(DataContext as RepoFilePreviewViewModel);
        SvgViewport.AttachScrollHost(SvgScrollViewer);
        QueueLoad();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _isAttached = false;
        SubscribeToViewModel(null);
        _requestGate.CancelCurrent();
        SvgViewport.Clear();
    }

    private void QueueLoad()
    {
        if (!_isAttached)
        {
            return;
        }

        byte[]? bytes = _viewModel?.Bytes;
        SvgPreviewRequest request = _requestGate.Begin();
        SvgViewport.Clear();
        ErrorText.Visibility = Visibility.Collapsed;
        AutomationProperties.SetItemStatus(ErrorText, string.Empty);
        AutomationProperties.SetHelpText(ErrorText, string.Empty);
        SvgViewport.Visibility = Visibility.Collapsed;
        _ = LoadSvgAsync(bytes, request);
    }

    private async Task LoadSvgAsync(byte[]? bytes, SvgPreviewRequest request)
    {
        RepositorySvgDocument? document = null;
        try
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(
                request.CancellationToken);
            deadline.CancelAfter(ParseDeadline);
            document = await Task.Run(
                () => _rasterizer.Load(bytes, deadline.Token),
                deadline.Token).ConfigureAwait(false);

            RepositorySvgDocument? published = document;
            await RunOnUiAsync(() =>
            {
                if (!_isAttached || !_requestGate.IsCurrent(request) || published is null)
                {
                    return;
                }

                SvgViewport.SetDocument(published, _rasterizer);
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
                    }
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            await ShowUnavailableIfCurrentAsync(request).ConfigureAwait(false);
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

    private Task ShowUnavailableIfCurrentAsync(SvgPreviewRequest request) => RunOnUiAsync(() =>
    {
        if (_requestGate.IsCurrent(request))
        {
            ShowUnavailable();
        }
    });

    private void SvgViewport_RenderFailed(object? sender, AppSvgRenderFailedEventArgs e)
    {
        if (_isAttached)
        {
            ShowUnavailable();
            AutomationProperties.SetItemStatus(
                ErrorText,
                $"render-failed:{e.Exception.GetType().Name}:0x{e.Exception.HResult:x8}");
            AutomationProperties.SetHelpText(ErrorText, e.Exception.Message);
        }
    }

    private void ShowUnavailable()
    {
        SvgViewport.Clear();
        ErrorText.Visibility = Visibility.Visible;
        SvgViewport.Visibility = Visibility.Collapsed;
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
