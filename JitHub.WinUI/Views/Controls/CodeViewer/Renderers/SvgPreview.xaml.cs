using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using SvgSkia = Svg.Skia;

namespace JitHub.WinUI.Views.Controls.CodeViewer.Renderers;

/// <summary>
/// Renders validated, self-contained repository SVG files without allowing stale loads to publish.
/// </summary>
public sealed partial class SvgPreview : UserControl
{
    private static readonly TimeSpan ParseDeadline = TimeSpan.FromSeconds(2);

    private readonly DispatcherQueue _dispatcher;
    private readonly SvgPreviewRequestGate _requestGate = new();
    private RepoFilePreviewViewModel? _viewModel;
    private SvgSkia.SKSvg? _svg;
    private bool _isAttached;

    public SvgPreview()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as RepoFilePreviewViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        QueueLoad();
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
        QueueLoad();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _isAttached = false;
        _requestGate.CancelCurrent();
        ReplaceSvg(null);
    }

    private void QueueLoad()
    {
        if (!_isAttached)
        {
            return;
        }

        byte[]? bytes = _viewModel?.Bytes;
        SvgPreviewRequest request = _requestGate.Begin();
        ReplaceSvg(null);
        ErrorText.Visibility = Visibility.Collapsed;
        SvgCanvas.Visibility = Visibility.Collapsed;
        _ = LoadSvgAsync(bytes, request);
    }

    private async Task LoadSvgAsync(byte[]? bytes, SvgPreviewRequest request)
    {
        SvgLoadResult result = SvgLoadResult.Unavailable;
        try
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(
                request.CancellationToken);
            deadline.CancelAfter(ParseDeadline);
            result = await Task.Run(
                () => ParseValidatedSvg(bytes, deadline.Token),
                deadline.Token).ConfigureAwait(false);

            await RunOnUiAsync(() => PublishResult(request, result)).ConfigureAwait(false);
            result = SvgLoadResult.Unavailable;
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            await RunOnUiAsync(() =>
            {
                if (_requestGate.IsCurrent(request))
                {
                    ShowUnavailable();
                }
            }).ConfigureAwait(false);
        }
        catch
        {
            await RunOnUiAsync(() =>
            {
                if (_requestGate.IsCurrent(request))
                {
                    ShowUnavailable();
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            result.Dispose();
            _requestGate.Complete(request);
        }
    }

    private static SvgLoadResult ParseValidatedSvg(byte[]? bytes, CancellationToken cancellationToken)
    {
        RepositorySvgValidationResult validation = RepositorySvgSecurityPolicy.Validate(bytes, cancellationToken);
        if (!validation.Accepted || bytes is null)
        {
            return SvgLoadResult.Unavailable;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SvgSkia.SKSvg? svg = null;
        try
        {
            svg = new SvgSkia.SKSvg();
            using MemoryStream stream = new(bytes, writable: false);
            svg.Load(stream);
            cancellationToken.ThrowIfCancellationRequested();

            SKPicture? picture = svg.Picture;
            if (picture is null ||
                !RepositorySvgSecurityPolicy.ArePictureBoundsSafe(
                    picture.CullRect.Width,
                    picture.CullRect.Height))
            {
                DisposeSvg(svg);
                return SvgLoadResult.Unavailable;
            }

            return new SvgLoadResult(svg);
        }
        catch (OperationCanceledException)
        {
            DisposeSvg(svg);
            throw;
        }
        catch
        {
            DisposeSvg(svg);
            return SvgLoadResult.Unavailable;
        }
    }

    private void PublishResult(SvgPreviewRequest request, SvgLoadResult result)
    {
        if (!_isAttached || !_requestGate.IsCurrent(request) || result.Svg is null)
        {
            return;
        }

        ReplaceSvg(result.TakeSvg());
        ErrorText.Visibility = Visibility.Collapsed;
        SvgCanvas.Visibility = Visibility.Visible;
        SvgCanvas.Invalidate();
    }

    private void SvgCanvas_PaintSurface(object? sender, SKPaintSurfaceEventArgs args)
    {
        SKCanvas canvas = args.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        SKPicture? picture = _svg?.Picture;
        if (picture is null)
        {
            return;
        }

        SKRect bounds = picture.CullRect;
        if (!RepositorySvgSecurityPolicy.ArePictureBoundsSafe(bounds.Width, bounds.Height))
        {
            return;
        }

        float scaleX = args.Info.Width / bounds.Width;
        float scaleY = args.Info.Height / bounds.Height;
        float scale = Math.Min(scaleX, scaleY);

        canvas.Save();
        canvas.Translate(-bounds.Left * scale, -bounds.Top * scale);
        canvas.Scale(scale, scale);
        canvas.DrawPicture(picture);
        canvas.Restore();
    }

    private void ShowUnavailable()
    {
        ReplaceSvg(null);
        ErrorText.Visibility = Visibility.Visible;
        SvgCanvas.Visibility = Visibility.Collapsed;
    }

    private void ReplaceSvg(SvgSkia.SKSvg? replacement)
    {
        SvgSkia.SKSvg? previous = _svg;
        _svg = replacement;
        if (!ReferenceEquals(previous, replacement))
        {
            DisposeSvg(previous);
        }
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

    private static void DisposeSvg(SvgSkia.SKSvg? svg)
    {
        if (svg is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private sealed class SvgLoadResult : IDisposable
    {
        public static SvgLoadResult Unavailable => new(null);

        public SvgLoadResult(SvgSkia.SKSvg? svg)
        {
            Svg = svg;
        }

        public SvgSkia.SKSvg? Svg { get; private set; }

        public SvgSkia.SKSvg? TakeSvg()
        {
            SvgSkia.SKSvg? svg = Svg;
            Svg = null;
            return svg;
        }

        public void Dispose()
        {
            DisposeSvg(Svg);
            Svg = null;
        }
    }
}
