using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.RenderResources;

/// <summary>
/// A software device, body and caption theme, and render resources for image
/// native retirement tests that build placeholder-only images without a source.
/// </summary>
internal sealed class ImageNativeRetirementHarness : IDisposable
{
    private readonly MarkdownRenderResources _renderResources = new();

    private readonly IReadOnlyDictionary<string, bool> _disclosures =
        new Dictionary<string, bool>(StringComparer.Ordinal);
    private readonly ThemeSnapshot _theme;

    internal ImageNativeRetirementHarness()
    {
        Device = new CanvasDevice(forceSoftwareRenderer: true);
        var body = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.ImageCaption] = body,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        _theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1f);
    }

    internal CanvasDevice Device { get; }

    internal MarkdownRenderResources RenderResources => _renderResources;

    internal MarkdownLayoutContext CreateContext() => new(
        Device,
        _theme,
        new MarkdownSourceMap("image native retirement"),
        new MarkdownExtensionRegistry(),
        FlowDirection.LeftToRight,
        language: "en-US")
    {
        RenderResources = _renderResources,
        DisclosureStates = _disclosures,
    };

    public void Dispose()
    {
        _renderResources.Images.BitmapCache.ReleaseDevice(Device);
        Device.Dispose();
    }
}
