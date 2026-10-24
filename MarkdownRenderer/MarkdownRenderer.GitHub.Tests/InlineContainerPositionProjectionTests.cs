using System.Reflection;
using System.Threading;
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

public sealed class InlineContainerPositionProjectionTests
{
    [Fact]
    public async Task UnmeasuredProjectionUsesUtf16RunLengthsWithoutBuildingTextBuffer()
    {
        MarkdownLayoutContext context = CreateContext(CancellationToken.None);
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var image = new InlineImageRun(context, "image", string.Empty);
        paragraph.Add(new TextRun("A😀"));
        var lineBreak = new LineBreakRun(isHard: true) { UseSingleLineLayout = true };
        paragraph.Add(lineBreak);
        paragraph.Add(new InlineEmbedRun(24, 24, static () => throw new InvalidOperationException()));
        paragraph.Add(image);
        paragraph.Add(new TextRun("e\u0301😀Z"));

        try
        {
            AssertBufferUnmaterialized(paragraph);

            Assert.Equal(6, paragraph.GetBufferCharOffset(new DocumentPosition(paragraph.BlockIndex, 4, 0)));
            Assert.Equal(9, paragraph.GetBufferCharOffset(new DocumentPosition(paragraph.BlockIndex, 4, 3)));
            Assert.Equal(11, paragraph.GetBufferCharOffset(new DocumentPosition(paragraph.BlockIndex, 4, 99)));
            Assert.Equal(new DocumentPosition(paragraph.BlockIndex, 1, 0),
                paragraph.GetPositionFromBufferOffset(3)); // exact boundary after UTF-16 emoji text
            Assert.Equal(new DocumentPosition(paragraph.BlockIndex, 2, 0),
                paragraph.GetPositionFromBufferOffset(4)); // newline is one UTF-16 unit
            Assert.Equal(new DocumentPosition(paragraph.BlockIndex, 3, 0),
                paragraph.GetPositionFromBufferOffset(5)); // embed placeholder is atomic, one unit
            Assert.Equal(new DocumentPosition(paragraph.BlockIndex, 4, 0),
                paragraph.GetPositionFromBufferOffset(6)); // image placeholder is atomic, one unit
            Assert.Equal(new DocumentPosition(paragraph.BlockIndex, 4, 3),
                paragraph.GetPositionFromBufferOffset(9)); // combining mark + surrogate pair
            Assert.Equal(new DocumentPosition(paragraph.BlockIndex, 4, 5),
                paragraph.GetPositionFromBufferOffset(int.MaxValue));

            AssertBufferUnmaterialized(paragraph);
        }
        finally
        {
            paragraph.Dispose();
            await image.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task DetachedUnmeasuredProjectionUsesReindexedRunsWithoutBuildingTextBuffer()
    {
        MarkdownLayoutContext context = CreateContext(CancellationToken.None);
        var staging = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var destination = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var image = new InlineImageRun(context, "image", string.Empty);
        staging.Add(new TextRun("😀"));
        staging.Add(image);
        staging.Add(new LineBreakRun(isHard: true));
        staging.Add(new TextRun("tail"));

        try
        {
            AssertBufferUnmaterialized(staging);
            InlineImageRun[] detached = staging.DetachInlineImagesForTransfer();
            Assert.Same(image, Assert.Single(detached));
            destination.Add(image);

            Assert.Equal(new[] { 0, 1, 2 }, staging.Runs.Select(static run => run.InlineIndex));
            Assert.Equal(3, staging.GetBufferCharOffset(new DocumentPosition(staging.BlockIndex, 2, 0)));
            Assert.Equal(new DocumentPosition(staging.BlockIndex, 2, 0),
                staging.GetPositionFromBufferOffset(3));
            Assert.Equal(new DocumentPosition(staging.BlockIndex, 2, 4),
                staging.GetPositionFromBufferOffset(int.MaxValue));
            AssertBufferUnmaterialized(staging);
        }
        finally
        {
            staging.Dispose();
            destination.Dispose();
            await image.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void CanceledContextDoesNotBlockMetadataOnlyPositionProjection()
    {
        using var cancellation = new CancellationTokenSource();
        MarkdownLayoutContext context = CreateContext(cancellation.Token);
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        paragraph.Add(new TextRun("already admitted text"));
        cancellation.Cancel();

        try
        {
            Assert.Equal(7, paragraph.GetBufferCharOffset(new DocumentPosition(paragraph.BlockIndex, 0, 7)));
            Assert.Equal(new DocumentPosition(paragraph.BlockIndex, 0, 7),
                paragraph.GetPositionFromBufferOffset(7));
            AssertBufferUnmaterialized(paragraph);
        }
        finally
        {
            paragraph.Dispose();
        }
    }

    private static void AssertBufferUnmaterialized(InlineContainerBox paragraph)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo dirtyField = Assert.IsAssignableFrom<FieldInfo>(
            typeof(InlineContainerBox).GetField("_bufferDirty", Flags));
        FieldInfo bufferField = Assert.IsAssignableFrom<FieldInfo>(
            typeof(InlineContainerBox).GetField("_buffer", Flags));

        Assert.True(Assert.IsType<bool>(dirtyField.GetValue(paragraph)));
        Assert.Equal(string.Empty, Assert.IsType<string>(bufferField.GetValue(paragraph)));
    }

    private static MarkdownLayoutContext CreateContext(CancellationToken cancellationToken)
    {
        var style = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.Link] = style,
            [MarkdownElementKeys.Math] = style,
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
            new MarkdownSourceMap(new string(' ', 1024)),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight)
        {
            CancellationToken = cancellationToken,
        };
    }
}
