using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ImageBaseUriIntegrationTests
{
    [Fact]
    public async Task RelativeFileSvgLoadsThroughTheResolvedBaseUri()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string assetDirectory = Path.Combine(root, "assets");
            Directory.CreateDirectory(assetDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(assetDirectory, "icon.svg"),
                "<svg xmlns='http://www.w3.org/2000/svg' width='12' height='8'>" +
                "<title>Local icon</title><rect width='12' height='8' fill='#336699'/></svg>");

            var image = new ImageBox(
                CreateContext(new Uri(Path.Combine(root, "readme.md"))),
                "assets/icon.svg",
                "local icon");
            try
            {
                await WaitForLoadAsync(image);

                Assert.NotNull(image.Bitmap);
                Assert.True(image.IsSvg);
                Assert.Equal("Local icon", image.SvgTitle);
            }
            finally
            {
                image.Dispose();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IdenticalRelativeSourcesUnderDifferentBasesDoNotShareSvgCacheEntries()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string firstRoot = Path.Combine(root, "first");
            string secondRoot = Path.Combine(root, "second");
            Directory.CreateDirectory(Path.Combine(firstRoot, "assets"));
            Directory.CreateDirectory(Path.Combine(secondRoot, "assets"));
            await File.WriteAllTextAsync(
                Path.Combine(firstRoot, "assets", "icon.svg"),
                CreateSvg("First document"));
            await File.WriteAllTextAsync(
                Path.Combine(secondRoot, "assets", "icon.svg"),
                CreateSvg("Second document"));

            var first = new ImageBox(
                CreateContext(new Uri(Path.Combine(firstRoot, "readme.md"))),
                "assets/icon.svg",
                "first");
            try
            {
                await WaitForLoadAsync(first);
                Assert.Equal("First document", first.SvgTitle);
            }
            finally
            {
                first.Dispose();
            }

            var second = new ImageBox(
                CreateContext(new Uri(Path.Combine(secondRoot, "readme.md"))),
                "assets/icon.svg",
                "second");
            try
            {
                await WaitForLoadAsync(second);

                Assert.NotNull(second.Bitmap);
                Assert.Equal("Second document", second.SvgTitle);
            }
            finally
            {
                second.Dispose();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitForLoadAsync(ImageBox image)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadCompletedEventArgs> handler = (_, _) => completion.TrySetResult();
        image.LoadCompleted += handler;
        try
        {
            image.EnsureLoading();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            image.LoadCompleted -= handler;
        }
    }

    private static MarkdownLayoutContext CreateContext(Uri imageBaseUri)
    {
        var style = new ElementStyle();
        string[] keys =
        [
            MarkdownElementKeys.Body,
            MarkdownElementKeys.ImageCaption,
            MarkdownElementKeys.ListMarker,
        ];
        var styles = keys.ToDictionary(static key => key, _ => style, StringComparer.Ordinal);
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
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
            ImageBaseUri = imageBaseUri,
            ImageCancellationToken = CancellationToken.None,
        };
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "MarkdownRenderer.ImageBaseUriTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateSvg(string title) =>
        $"<svg xmlns='http://www.w3.org/2000/svg' width='12' height='8'>" +
        $"<title>{title}</title><rect width='12' height='8' fill='#336699'/></svg>";
}
