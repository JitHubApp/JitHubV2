using System;
using System.Collections.Generic;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Keyboard and UI Automation activation of a focused linked image follow the
/// image's link; an unlinked image is not an activation target.
/// </summary>
public sealed class LinkedAtomicTargetTests
{
    [Fact]
    public void LinkedImageActivatesItsLinkWithItsTitleAndSourceSpan()
    {
        var image = new InlineImageRun(
            CreateContext(),
            altText: string.Empty,
            url: string.Empty,
            linkUrl: "https://example.test/image-target",
            linkTitle: "Image target")
        {
            SourceSpan = new SourceSpan(12, 40),
        };

        Assert.True(LinkTargets.TryCreateLinkedAtomicTarget(image, out LinkTarget target));
        Assert.Equal("https://example.test/image-target", target.Url);
        Assert.Equal("Image target", target.Title);
        Assert.Equal(new SourceSpan(12, 40), target.SourceSpan);
        Assert.Null(target.Run);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnlinkedImageIsNotAnActivationTarget(string? linkUrl)
    {
        var image = new InlineImageRun(
            CreateContext(),
            altText: "ordinary image",
            url: string.Empty,
            linkUrl: linkUrl);

        Assert.False(LinkTargets.TryCreateLinkedAtomicTarget(image, out _));
    }

    private static MarkdownLayoutContext CreateContext()
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.ImageCaption] = style,
        };
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
            textScaleFactor: 1f);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(new string('x', 64)),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }
}
