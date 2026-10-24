using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// P3-022 (Phase 3 ranks 164, 206, 273, 340, 384): every published Mermaid or
/// Math scene rebuilds the document. A fresh image box whose pixels had been
/// released offscreen reported Loading again and reserved a placeholder size,
/// so readers saw placeholders and reflowed content for images that had
/// already loaded. A rebuild of the same document now carries each completed
/// image's result: the fresh box reports Loaded and measures with the size the
/// image already had, but visible readiness still waits for its pixels.
/// </summary>
public sealed class CompletedImageCarryTests
{
    private const string BadgeUrl = "https://img.example.test/badge.svg";
    private const string PhotoUrl = "https://img.example.test/photo.png";

    [Fact]
    public void FreshBoxWithoutACarriedResultLoadsWithAPlaceholderSize()
    {
        var image = new ImageBox(CreateContext(carry: null), PhotoUrl, "Photo");

        Assert.Equal(MarkdownImageAccessibilityState.Loading, image.AccessibilityState);
        Assert.True(image.IsAwaitingVisiblePixelsForAutomation);
        Size inline = image.MeasureInline(400, 20);
        Assert.Equal(20, inline.Height, 3);
        Assert.False(image.TryCaptureCompletedCarry(out CompletedImageCarry? carry));
        Assert.Null(carry);
    }

    [Fact]
    public void CarriedRasterResultKeepsItsStateAndSizeButStillAwaitsPixels()
    {
        var carried = new CompletedImageCarry(PhotoUrl, IsSvg: false, new Size(320, 180), null, null);
        var image = new ImageBox(CreateContext(Carry(carried)), PhotoUrl, "Photo");

        Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
        Assert.True(image.IsAwaitingVisiblePixelsForAutomation,
            "Visible readiness must wait for the carried image's pixels.");
        Size inline = image.MeasureInline(400, 20);
        Assert.Equal(320, inline.Width, 3);
        Assert.Equal(180, inline.Height, 3);
        // A narrower column scales the carried size exactly like a decoded image.
        Size narrow = image.MeasureInline(160, 20);
        Assert.Equal(160, narrow.Width, 3);
        Assert.Equal(90, narrow.Height, 3);
    }

    [Fact]
    public void CarriedSvgResultKeepsItsBlockSizeTitleAndDescription()
    {
        var carried = new CompletedImageCarry(BadgeUrl, IsSvg: true, new Size(90, 20), "Build passing", "CI status");
        var image = new ImageBox(CreateContext(Carry(carried)), BadgeUrl, string.Empty);

        float height = image.Measure(600);
        Assert.Equal(90, image.AutomationRenderDestination.Width, 3);
        Assert.Equal(20, image.AutomationRenderDestination.Height, 3);
        Assert.True(height >= 20);
        Assert.Equal("Build passing", image.SvgTitle);
        Assert.Equal("CI status", image.SvgDesc);
        Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
    }

    [Fact]
    public void UnpresentedBoxForwardsItsCarriedResultToTheNextRebuild()
    {
        var carried = new CompletedImageCarry(PhotoUrl, IsSvg: false, new Size(320, 180), null, null);
        var image = new ImageBox(CreateContext(Carry(carried)), PhotoUrl, "Photo");

        Assert.True(image.TryCaptureCompletedCarry(out CompletedImageCarry? forwarded));
        Assert.Same(carried, forwarded);
    }

    [Fact]
    public void CarriedResultIsIgnoredForAnotherUrlOrAnUnusableSize()
    {
        var other = new CompletedImageCarry(BadgeUrl, IsSvg: true, new Size(90, 20), null, null);
        var otherUrlImage = new ImageBox(CreateContext(Carry(other)), PhotoUrl, "Photo");
        Assert.Equal(MarkdownImageAccessibilityState.Loading, otherUrlImage.AccessibilityState);

        var empty = new CompletedImageCarry(PhotoUrl, IsSvg: false, new Size(0, 180), null, null);
        var emptySizeImage = new ImageBox(CreateContext(Carry(empty)), PhotoUrl, "Photo");
        Assert.Equal(MarkdownImageAccessibilityState.Loading, emptySizeImage.AccessibilityState);
    }

    [Fact]
    public void RasterPlacementContextKeepsTheCarriedResults()
    {
        var carried = new CompletedImageCarry(PhotoUrl, IsSvg: false, new Size(320, 180), null, null);
        MarkdownLayoutContext context = CreateContext(Carry(carried));
        MarkdownLayoutContext placement = context.CreateRasterPlacementContext(
            CancellationToken.None, new object(), retirementSink: null, imageUnavailable: null);

        Assert.Same(context.ImageCarryIdentity, placement.ImageCarryIdentity);
        Assert.Same(context.CompletedImageCarry, placement.CompletedImageCarry);
    }

    [Fact]
    public void CarryIdentityRequiresTheSameSourceAndImageConfiguration()
    {
        object resolver = new();
        object svg = new();
        object raster = new();
        var baseUri = new Uri("https://github.test/o/r/blob/main/");
        var source = new MarkdownDocumentSource("doc", "o", "r", "main", "README.md");
        MarkdownImageCarryIdentity Create(
            string text = "# A",
            Uri? uri = null,
            object? imageResolver = null,
            bool thirdParty = false,
            MarkdownDocumentSource? documentSource = null) =>
            new(text, uri ?? baseUri, "README.md", documentSource ?? source, imageResolver ?? resolver, svg, raster, thirdParty);

        MarkdownImageCarryIdentity identity = Create();
        Assert.True(identity.Matches(Create()));
        Assert.True(identity.Matches(identity));
        Assert.False(identity.Matches(null));
        Assert.False(identity.Matches(Create(text: "# B")));
        Assert.False(identity.Matches(Create(uri: new Uri("https://github.test/o/r/blob/dev/"))));
        Assert.False(identity.Matches(Create(imageResolver: new object())));
        Assert.False(identity.Matches(Create(thirdParty: true)));
        Assert.False(identity.Matches(Create(documentSource: source with { Ref = "dev" })));
    }

    [Fact]
    public void ControlCarriesCompletedImagesIntoEveryRebuildOfTheSameDocument()
    {
        string control = ReadSource("Controls", "MarkdownRendererControl.cs");
        string rebuild = Body(control, "private async Task RebuildInternalAsync(");
        int capture = rebuild.IndexOf("CaptureCompletedImageCarry(imageCarryIdentity)", StringComparison.Ordinal);
        int context = rebuild.IndexOf("var ctx = new MarkdownLayoutContext(", StringComparison.Ordinal);
        Assert.True(capture >= 0 && context > capture, "The ledger must be captured before the rebuild's context.");
        Assert.Contains("CompletedImageCarry = completedImageCarrySnapshot,", rebuild, StringComparison.Ordinal);
        Assert.Contains("ImageCarryIdentity = imageCarryIdentity,", rebuild, StringComparison.Ordinal);
        Assert.Contains("var imageResolverIdentity = imageResolverSnapshot;", rebuild, StringComparison.Ordinal);

        string captureBody = Body(control, "private IReadOnlyDictionary<string, Layout.Boxes.CompletedImageCarry>? CaptureCompletedImageCarry(");
        Assert.Contains("_completedImageCarry.Clear();", captureBody, StringComparison.Ordinal);
        Assert.Contains("builtWith.Matches(identity)", captureBody, StringComparison.Ordinal);

        // Visible readiness never accepts a carried Loaded state without pixels.
        string visible = Body(control, "internal bool HasVisibleLoadingImagesForAutomation()");
        Assert.Contains("image.IsAwaitingVisiblePixelsForAutomation", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkdownImageAccessibilityState.Loading", visible, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, CompletedImageCarry> Carry(CompletedImageCarry carry) =>
        new Dictionary<string, CompletedImageCarry>(StringComparer.Ordinal) { [carry.Url] = carry };

    private static MarkdownLayoutContext CreateContext(IReadOnlyDictionary<string, CompletedImageCarry>? carry)
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
            new MarkdownSourceMap(new string(' ', 2_000)),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            ImageCarryIdentity = new MarkdownImageCarryIdentity(
                "# A", null, null, null, null, null, null, allowThirdPartyRemoteImages: false),
            CompletedImageCarry = carry,
        };
    }

    private static string ReadSource(string folder, string fileName)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", folder, fileName);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException($"Could not locate {fileName}.");
    }

    private static string Body(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find {signature}");
        int open = source.IndexOf('{', member + signature.Length);
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0) return source[open..(index + 1)];
        }
        throw new InvalidOperationException("Unbalanced source block.");
    }
}
