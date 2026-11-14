using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;

/// <summary>
/// Builds the layout contexts, SVG sources and preparation sessions that
/// the SVG image box tests load, waits for loads and conditions, and
/// observes cleanup tasks without masking the original failure.
/// </summary>
internal static class ImageBoxSvgRendererFixture
{
    internal static async Task WaitForLoadAsync(ImageBox image)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadCompletedEventArgs> handler = (_, _) => completion.TrySetResult();
        image.LoadCompleted += handler;
        try
        {
            image.EnsureLoading();
            await completion.Task.WaitAsync(AsyncTestWait.HangGuard);
        }
        finally
        {
            image.LoadCompleted -= handler;
        }
    }

    internal static Task WaitUntilAsync(Func<bool> condition) =>
        AsyncTestWait.UntilAsync(condition);

    internal static async Task ObserveTaskForCleanupAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(AsyncTestWait.HangGuard);
        }
        catch
        {
            // A cleanup wait must observe eventual faults/timeouts without
            // hiding the assertion that originally caused test unwinding.
        }
    }

    internal static MarkdownPerformanceSession CreateSingleSlotCpuPreparationSession() =>
        new(MarkdownPerformanceOptions.Progressive with
        {
            MaxConcurrentCpuPreparations = 1,
        });

    internal static MarkdownLayoutContext CreateContext(
        MarkdownRenderResources renderResources,
        IMarkdownSvgRenderer? renderer,
        double rasterizationScale = 1,
        IMarkdownImageResolver? imageResolver = null,
        Action<string, MarkdownImageUnavailableReason, MarkdownSvgFailureReason?>? imageUnavailable = null,
        MarkdownPerformanceSession? performanceSession = null,
        object? performanceDocumentOwner = null)
    {
        var body = new ElementStyle
        {
            Foreground = Color.FromArgb(255, 12, 34, 56),
        };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.ImageCaption] = body,
            [MarkdownElementKeys.ListMarker] = body,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: true,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            RenderResources = renderResources,
            ImageResolver = imageResolver,
            PerformanceSession = performanceSession,
            PerformanceDocumentOwner = performanceDocumentOwner,
            ImageUnavailable = imageUnavailable,
            SvgRenderer = renderer,
            ImageCancellationToken = CancellationToken.None,
            RasterizationScale = rasterizationScale,
        };
    }

    internal static string CreateSvgDataUri(string marker)
    {
        string svg = CreateSvgMarkup(marker);
        return "data:image/svg+xml," + Uri.EscapeDataString(svg);
    }

    internal static string CreateSvgMarkup(string title) =>
        $"<svg xmlns='http://www.w3.org/2000/svg' width='200' height='100'>" +
        $"<title>{title}</title><rect width='200' height='100'/></svg>";
}
