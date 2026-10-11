using System.Runtime.CompilerServices;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class CompactSemanticSpanTests
{
    [Fact]
    public void CommonSemanticSpanIsAnInlineValueWithOneOwnerSlot()
    {
        Assert.True(typeof(MarkdownTextSpan).IsValueType);
        Assert.InRange(Unsafe.SizeOf<MarkdownTextSpan>(), 1, 40);
    }

    [Fact]
    public void SingleBoxOwnerKeepsItsTypedAccessorAndIndependentMetadata()
    {
        MarkdownLayoutContext context = CreateContext();
        var inline = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var run = new TextRun("source text");
        var style = new ElementStyle();

        var span = new MarkdownTextSpan(
            3,
            14,
            inline,
            run,
            null,
            null,
            null,
            VectorSemanticIndex: 7,
            VectorTextStyle: style);

        Assert.Equal(3, span.TextStart);
        Assert.Equal(14, span.TextEnd);
        Assert.Same(inline, span.InlineBox);
        Assert.Same(run, span.InlineRun);
        Assert.Null(span.ImageBox);
        Assert.Null(span.EmbedBox);
        Assert.Null(span.HostedElementBox);
        Assert.Null(span.VectorSceneBox);
        Assert.Equal(7, span.VectorSemanticIndex);
        Assert.Same(style, span.VectorTextStyle);
    }

    [Fact]
    public void MultipleBoxOwnersUseLosslessFallbackInsteadOfThrowingOrDroppingReferences()
    {
        MarkdownLayoutContext context = CreateContext();
        var inline = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var image = new ImageBox(context, string.Empty, "image alt")
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var run = new TextRun("atomic name");
        var style = new ElementStyle();

        var span = new MarkdownTextSpan(
            20,
            31,
            inline,
            run,
            image,
            null,
            null,
            VectorSemanticIndex: 11,
            VectorTextStyle: style);

        Assert.Same(inline, span.InlineBox);
        Assert.Same(image, span.ImageBox);
        Assert.Same(run, span.InlineRun);
        Assert.Null(span.EmbedBox);
        Assert.Null(span.HostedElementBox);
        Assert.Null(span.VectorSceneBox);
        Assert.Equal(11, span.VectorSemanticIndex);
        Assert.Same(style, span.VectorTextStyle);
    }

    [Fact]
    public void UnownedSpanKeepsVectorMetadataAndDefaultSentinel()
    {
        var style = new ElementStyle();
        var unowned = new MarkdownTextSpan(0, 0, null, null, null, null, null);
        var vectorMetadata = new MarkdownTextSpan(
            1,
            4,
            null,
            null,
            null,
            null,
            null,
            VectorSemanticIndex: 23,
            VectorTextStyle: style);

        Assert.Equal(-1, unowned.VectorSemanticIndex);
        Assert.Null(unowned.VectorTextStyle);
        Assert.Null(unowned.InlineBox);
        Assert.Null(unowned.ImageBox);
        Assert.Null(unowned.EmbedBox);
        Assert.Null(unowned.HostedElementBox);
        Assert.Null(unowned.VectorSceneBox);
        Assert.Equal(23, vectorMetadata.VectorSemanticIndex);
        Assert.Same(style, vectorMetadata.VectorTextStyle);
    }

    private static MarkdownLayoutContext CreateContext()
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.Diagram] = style,
            [MarkdownElementKeys.Link] = style,
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
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(new string(' ', 32)),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }
}
