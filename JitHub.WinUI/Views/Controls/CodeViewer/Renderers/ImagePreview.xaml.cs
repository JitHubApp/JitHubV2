using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace JitHub.WinUI.Views.Controls.CodeViewer.Renderers;

/// <summary>
/// Renders image files (PNG, JPG, GIF, BMP, ICO, TIFF, HEIF, WebP) in a
/// zoomable ScrollViewer. DataContext must be a <see cref="RepoFilePreviewViewModel"/>.
/// </summary>
public sealed partial class ImagePreview : UserControl
{
    private readonly DispatcherQueue _dispatcher;
    private byte[]? _lastBytes;

    public ImagePreview()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        DataContextChanged += OnDataContextChanged;
    }

    private RepoFilePreviewViewModel? ViewModel => DataContext as RepoFilePreviewViewModel;

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        LoadImage();
    }

    private void LoadImage()
    {
        var vm = ViewModel;
        var bytes = vm?.Bytes;
        _lastBytes = bytes;

        if (bytes is not { Length: > 0 })
        {
            ShowError();
            return;
        }

        UpdateFooter(vm!, null, null);

        _ = LoadImageAsync(bytes, vm!);
    }

    private async Task LoadImageAsync(byte[] bytes, RepoFilePreviewViewModel vm)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);

            var bitmap = new BitmapImage();
            bitmap.ImageOpened += (s, e) =>
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (_lastBytes != bytes) return;
                    UpdateFooter(vm, bitmap.PixelWidth, bitmap.PixelHeight);
                });
            };
            bitmap.ImageFailed += (s, e) =>
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (_lastBytes != bytes) return;
                    ShowError();
                });
            };

            await bitmap.SetSourceAsync(stream);

            _dispatcher.TryEnqueue(() =>
            {
                if (_lastBytes != bytes) return;
                PreviewImage.Source = bitmap;
                PreviewImage.Visibility = Visibility.Visible;
                ErrorText.Visibility = Visibility.Collapsed;
            });
        }
        catch
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_lastBytes == bytes) ShowError();
            });
        }
    }

    private void ShowError()
    {
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void UpdateFooter(RepoFilePreviewViewModel vm, int? width, int? height)
    {
        var mime = vm.ImageMimeType ?? "image";
        var size = FormatBytes(vm.ByteSize);
        var dims = (width.HasValue && height.HasValue) ? $"  ·  {width}×{height}" : string.Empty;
        FooterText.Text = $"{mime}  ·  {size}{dims}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024):F1} MB";
    }
}
