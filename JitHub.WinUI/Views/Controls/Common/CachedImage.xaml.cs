using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace JitHub.WinUI.Views.Controls.Common;

public sealed partial class CachedImage : UserControl
{
    public static readonly DependencyProperty SourceUrlProperty = DependencyProperty.Register(
        nameof(SourceUrl),
        typeof(string),
        typeof(CachedImage),
        new PropertyMetadata(string.Empty, OnSourceUrlChanged));

    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
        nameof(Stretch),
        typeof(Stretch),
        typeof(CachedImage),
        new PropertyMetadata(Stretch.Uniform));

    public static readonly DependencyProperty FallbackUrlProperty = DependencyProperty.Register(
        nameof(FallbackUrl),
        typeof(string),
        typeof(CachedImage),
        new PropertyMetadata("ms-appx:///Assets/Octocat.png"));

    private CancellationTokenSource? _loadCancellation;
    private long _loadVersion;
    private int _isLoaded;

    public CachedImage()
    {
        InitializeComponent();
        Loaded += CachedImage_Loaded;
        Unloaded += CachedImage_Unloaded;
    }

    public string SourceUrl
    {
        get => (string)GetValue(SourceUrlProperty);
        set => SetValue(SourceUrlProperty, value);
    }

    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public string FallbackUrl
    {
        get => (string)GetValue(FallbackUrlProperty);
        set => SetValue(FallbackUrlProperty, value);
    }

    private static void OnSourceUrlChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is CachedImage image)
        {
            _ = image.LoadAsync(args.NewValue as string);
        }
    }

    private async Task LoadAsync(string? sourceUrl)
    {
        long version = Interlocked.Increment(ref _loadVersion);
        CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _loadCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();

        try
        {
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                SetFallback(version);
                return;
            }

            if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? directUri) &&
                directUri.Scheme is not ("http" or "https"))
            {
                if (version == Volatile.Read(ref _loadVersion))
                {
                    ImageElement.Source = new BitmapImage(directUri);
                }

                return;
            }

            IGitHubImageService imageService =
                ((JitHub.WinUI.App)Application.Current).GetService<IGitHubImageService>();
            GitHubCachedImage? cachedImage = await imageService.GetAsync(sourceUrl, cancellation.Token);
            if (cachedImage is null || version != Volatile.Read(ref _loadVersion))
            {
                SetFallback(version);
                return;
            }

            try
            {
                await ApplyBytesAsync(cachedImage.Bytes, version, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                await imageService.InvalidateAsync(sourceUrl, CancellationToken.None);
                SetFallback(version);
                return;
            }

            if (cachedImage.RefreshTask is not null)
            {
                try
                {
                    GitHubCachedImage? refreshed = await cachedImage.RefreshTask.WaitAsync(cancellation.Token);
                    if (refreshed is not null)
                    {
                        await ApplyBytesAsync(refreshed.Bytes, version, cancellation.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Keep the stale image visible when background revalidation fails.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            SetFallback(version);
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _loadCancellation, null, cancellation), cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private async Task ApplyBytesAsync(byte[] bytes, long version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using InMemoryRandomAccessStream stream = new();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        BitmapImage bitmap = new();
        if (ActualWidth > 0)
        {
            double scale = XamlRoot?.RasterizationScale ?? 1;
            bitmap.DecodePixelWidth = (int)Math.Ceiling(ActualWidth * scale);
        }

        await bitmap.SetSourceAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();
        if (version == Volatile.Read(ref _loadVersion))
        {
            ImageElement.Source = bitmap;
        }
    }

    private void SetFallback(long version)
    {
        if (version != Volatile.Read(ref _loadVersion) ||
            !Uri.TryCreate(FallbackUrl, UriKind.Absolute, out Uri? fallbackUri))
        {
            return;
        }

        ImageElement.Source = new BitmapImage(fallbackUri);
    }

    private void CachedImage_Unloaded(object sender, RoutedEventArgs e)
    {
        Volatile.Write(ref _isLoaded, 0);
        _ = CancelIfStillUnloadedAsync();
    }

    private void CachedImage_Loaded(object sender, RoutedEventArgs e)
    {
        Volatile.Write(ref _isLoaded, 1);
        if (ImageElement.Source is null)
        {
            _ = LoadAsync(SourceUrl);
        }
    }

    private async Task CancelIfStillUnloadedAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        if (Volatile.Read(ref _isLoaded) != 0)
        {
            return;
        }

        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
