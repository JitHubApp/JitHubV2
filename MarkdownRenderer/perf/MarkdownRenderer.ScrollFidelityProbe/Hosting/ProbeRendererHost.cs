using MarkdownRenderer.Controls;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Performance;
using MarkdownRenderer.ScrollFidelityProbe.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MarkdownRenderer.ScrollFidelityProbe.Hosting;

/// <summary>
/// Owns the renderer under measurement. Every scenario starts from a fresh
/// <see cref="MarkdownScrollView"/> with its own engine, so no scenario
/// inherits another's measured layout, parse cache or painted tiles. The
/// view is configured the way JitHub configures its README views.
/// </summary>
internal sealed class ProbeRendererHost
{
    /// <summary>Scroll bar strip excluded from the measured viewport.</summary>
    private const double ScrollBarStripDips = 24;

    private readonly Grid _host;
    private readonly bool _progressive;
    private MarkdownScrollView? _renderer;
    private MarkdownPerformanceSession? _session;

    internal ProbeRendererHost(Grid host, bool progressive)
    {
        _host = host;
        _progressive = progressive;
    }

    internal MarkdownScrollView Renderer => _renderer ?? throw new InvalidOperationException("No renderer.");

    internal ScrollViewer ScrollViewer { get; private set; } = null!;

    /// <summary>Replaces the renderer with a fresh one showing <paramref name="markdown"/>.</summary>
    internal async Task<double> LoadFreshAsync(string markdown, CancellationToken cancellationToken)
    {
        await DisposeRendererAsync();
        var renderer = new MarkdownScrollView().UseGitHubReadme();
        renderer.HorizontalAlignment = HorizontalAlignment.Stretch;
        renderer.VerticalAlignment = VerticalAlignment.Stretch;
        if (_progressive)
        {
            _session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
            renderer.PerformanceSession = _session;
        }

        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        renderer.RenderCompleted += (_, _) => rendered.TrySetResult();
        renderer.RenderFailed += (_, args) => rendered.TrySetException(
            new InvalidOperationException($"Render failed: {args.Exception}"));
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _renderer = renderer;
        _host.Children.Add(renderer);
        renderer.Markdown = markdown;
        await rendered.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
        double firstRenderMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        ScrollViewer = FindScrollViewer(renderer) ??
            throw new InvalidOperationException("The renderer has no ScrollViewer.");
        return firstRenderMs;
    }

    internal async Task DisposeRendererAsync()
    {
        if (_renderer is not null)
        {
            MarkdownScrollView renderer = _renderer;
            _renderer = null;
            _host.Children.Remove(renderer);
            await renderer.DisposeAsync();
        }

        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
    }

    /// <summary>
    /// The renderer's viewport, minus the scroll bar strip, in physical pixels
    /// of the captured window frame.
    /// </summary>
    internal Rect GetCaptureRegion(nint window, double rasterizationScale)
    {
        MarkdownScrollView renderer = Renderer;
        Rect bounds = renderer.TransformToVisual(null).TransformBounds(
            new Rect(0, 0, Math.Max(1, renderer.ActualWidth - ScrollBarStripDips), renderer.ActualHeight));
        var client = new NativeMethods.Point();
        NativeMethods.ClientToScreen(window, ref client);
        NativeMethods.NativeRect frame = NativeMethods.GetExtendedFrameBounds(window);
        double left = client.X - frame.Left + bounds.X * rasterizationScale;
        double top = client.Y - frame.Top + bounds.Y * rasterizationScale;
        return new Rect(
            Math.Ceiling(left),
            Math.Ceiling(top),
            Math.Floor(bounds.Width * rasterizationScale) - 1,
            Math.Floor(bounds.Height * rasterizationScale) - 1);
    }

    /// <summary>The renderer's center in screen pixels, for wheel input.</summary>
    internal NativeMethods.Point GetScreenCenter(nint window, double rasterizationScale)
    {
        MarkdownScrollView renderer = Renderer;
        Point center = renderer.TransformToVisual(null).TransformPoint(
            new Point(renderer.ActualWidth / 2, renderer.ActualHeight / 2));
        var point = new NativeMethods.Point
        {
            X = (int)(center.X * rasterizationScale),
            Y = (int)(center.Y * rasterizationScale),
        };
        NativeMethods.ClientToScreen(window, ref point);
        return point;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer scrollViewer)
                return scrollViewer;
            if (FindScrollViewer(child) is { } nested)
                return nested;
        }

        return null;
    }
}
