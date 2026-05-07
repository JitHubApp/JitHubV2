using System;
using System.IO;
using System.Threading.Tasks;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using SvgSkia = Svg.Skia;

namespace JitHub.WinUI.Views.Controls.CodeViewer.Renderers;

/// <summary>
/// Renders SVG files via Svg.Skia + SKXamlCanvas inside a zoomable ScrollViewer.
/// DataContext must be a <see cref="RepoFilePreviewViewModel"/>.
/// </summary>
public sealed partial class SvgPreview : UserControl
{
    private readonly DispatcherQueue _dispatcher;
    private SvgSkia.SKSvg? _svg;
    private byte[]? _lastBytes;

    public SvgPreview()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        DataContextChanged += OnDataContextChanged;
    }

    private RepoFilePreviewViewModel? ViewModel => DataContext as RepoFilePreviewViewModel;

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        LoadSvg();
    }

    private void LoadSvg()
    {
        var vm = ViewModel;
        var bytes = vm?.Bytes;
        _lastBytes = bytes;
        _svg = null;

        if (bytes is not { Length: > 0 })
        {
            ShowError();
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var svg = new SvgSkia.SKSvg();
                svg.Load(new MemoryStream(bytes));
                return svg.Picture is not null ? svg : null;
            }
            catch
            {
                return null;
            }
        }).ContinueWith(t =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_lastBytes != bytes) return;

                if (t.Result is null)
                {
                    ShowError();
                    return;
                }

                _svg = t.Result;
                ErrorText.Visibility = Visibility.Collapsed;
                SvgCanvas.Visibility = Visibility.Visible;
                SvgCanvas.Invalidate();
            });
        }, TaskScheduler.Default);
    }

    private void SvgCanvas_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var picture = _svg?.Picture;
        if (picture is null) return;

        var cullRect = picture.CullRect;
        if (cullRect.Width <= 0 || cullRect.Height <= 0) return;

        float scaleX = e.Info.Width / cullRect.Width;
        float scaleY = e.Info.Height / cullRect.Height;
        float scale = Math.Min(scaleX, scaleY);

        canvas.Save();
        canvas.Scale(scale, scale);
        canvas.DrawPicture(picture);
        canvas.Restore();
    }

    private void ShowError()
    {
        _svg = null;
        ErrorText.Visibility = Visibility.Visible;
        SvgCanvas.Visibility = Visibility.Collapsed;
    }
}
