using System;
using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class InlineContainerMeasureTransactionTests
{
    [Fact]
    public void CancellationAfterCandidateCreationKeepsPreviousNativeTextLayoutUsable()
    {
        using var cancellation = new CancellationTokenSource();
        MarkdownLayoutContext context = CreateContext(cancellation.Token);
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var run = new CancelOnTextReadRun(cancellation);
        paragraph.Add(run);

        try
        {
            _ = paragraph.Measure(320f);
            paragraph.Arrange(12f, 24f, 320f);
            Rect committedBounds = paragraph.Bounds;
            Rect committedRunBounds = paragraph.GetRunRect(run.InlineIndex);
            Assert.True(committedRunBounds.Width > 0);

            run.CancelOnNextRead();

            Assert.Throws<OperationCanceledException>(() => paragraph.Measure(150f));

            Assert.True(paragraph.HasMeasuredLayout);
            Assert.Equal(committedBounds, paragraph.Bounds);
            Assert.Equal(committedRunBounds, paragraph.GetRunRect(run.InlineIndex));

            // The canceled candidate must not dirty the published layout state:
            // the next non-canceled measurement can still build and replace it.
            context.SetCancellationToken(CancellationToken.None);
            float nextHeight = paragraph.Measure(150f);
            Assert.True(float.IsFinite(nextHeight));
            Assert.True(nextHeight >= 0);
        }
        finally
        {
            paragraph.Dispose();
        }
    }

    [Fact]
    public async Task CancellationAfterInlineImageMeasureRestoresGeometryAndNativePlaceholder()
    {
        using var cancellation = new CancellationTokenSource();
        MarkdownLayoutContext context = CreateContext(cancellation.Token);
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var imageRun = new InlineImageRun(
            context,
            "transaction image",
            "transaction-image.png",
            requestedWidth: new SafeHtmlLength(260f, IsPercent: false));
        var cancelRun = new CancelOnTextReadRun(cancellation);
        paragraph.Add(imageRun);
        paragraph.Add(cancelRun);

        try
        {
            _ = paragraph.Measure(320f);
            paragraph.Arrange(12f, 24f, 320f);
            Rect oldParagraphBounds = paragraph.Bounds;
            Rect oldRunBounds = paragraph.GetRunRect(imageRun.InlineIndex);
            Assert.True(oldRunBounds.Width > 0);
            imageRun.Image.SetInlineBounds(new Rect(
                oldRunBounds.X,
                oldRunBounds.Y,
                imageRun.DesiredWidth,
                imageRun.DesiredHeight));

            Rect oldAutomationBounds = imageRun.Image.AutomationRenderDestination;
            float oldDesiredWidth = imageRun.DesiredWidth;
            float oldDesiredHeight = imageRun.DesiredHeight;
            float oldImageWidth = imageRun.Image.MeasuredImageWidth;
            float oldImageHeight = imageRun.Image.MeasuredImageHeight;
            CanvasTextLayout oldPlaceholder = Assert.IsType<CanvasTextLayout>(
                imageRun.Image.PlaceholderLayoutForTests);
            Windows.Foundation.Rect oldPlaceholderBounds = oldPlaceholder.LayoutBounds;

            cancelRun.CancelOnNextRead();
            Assert.Throws<OperationCanceledException>(() => paragraph.Measure(150f));

            Assert.Equal(oldDesiredWidth, imageRun.DesiredWidth);
            Assert.Equal(oldDesiredHeight, imageRun.DesiredHeight);
            Assert.Equal(oldImageWidth, imageRun.Image.MeasuredImageWidth);
            Assert.Equal(oldImageHeight, imageRun.Image.MeasuredImageHeight);
            Assert.Equal(oldParagraphBounds, paragraph.Bounds);
            Assert.Equal(oldRunBounds, paragraph.GetRunRect(imageRun.InlineIndex));
            Assert.Equal(oldAutomationBounds, imageRun.Image.AutomationRenderDestination);
            Assert.Same(oldPlaceholder, imageRun.Image.PlaceholderLayoutForTests);
            Assert.Equal(oldPlaceholderBounds, oldPlaceholder.LayoutBounds);

            // The failed candidate did not poison the next reflow: this commit
            // may retire the old placeholder only after the replacement layout
            // has completed successfully.
            context.SetCancellationToken(CancellationToken.None);
            float replacementHeight = paragraph.Measure(150f);
            Assert.True(float.IsFinite(replacementHeight));
            Assert.True(replacementHeight >= 0);
            Assert.True(imageRun.DesiredWidth < oldDesiredWidth);
            Assert.True(imageRun.Image.MeasuredImageWidth < oldImageWidth);
            CanvasTextLayout replacementPlaceholder = Assert.IsType<CanvasTextLayout>(
                imageRun.Image.PlaceholderLayoutForTests);
            Assert.NotSame(oldPlaceholder, replacementPlaceholder);
            Assert.True(replacementPlaceholder.LayoutBounds.Width >= 0);
        }
        finally
        {
            paragraph.Dispose();
            await imageRun.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task IntrinsicWidthQueryDoesNotReplacePublishedInlineImageState()
    {
        using var cancellation = new CancellationTokenSource();
        MarkdownLayoutContext context = CreateContext(cancellation.Token);
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var imageRun = new InlineImageRun(
            context,
            "intrinsic image",
            "intrinsic-image.png",
            requestedWidth: new SafeHtmlLength(260f, IsPercent: false));
        var cancelRun = new CancelOnTextReadRun(cancellation);
        paragraph.Add(imageRun);
        paragraph.Add(cancelRun);

        try
        {
            _ = paragraph.Measure(320f);
            paragraph.Arrange(12f, 24f, 320f);
            Rect oldRunBounds = paragraph.GetRunRect(imageRun.InlineIndex);
            imageRun.Image.SetInlineBounds(new Rect(
                oldRunBounds.X,
                oldRunBounds.Y,
                imageRun.DesiredWidth,
                imageRun.DesiredHeight));

            Rect oldAutomationBounds = imageRun.Image.AutomationRenderDestination;
            float oldDesiredWidth = imageRun.DesiredWidth;
            float oldDesiredHeight = imageRun.DesiredHeight;
            float oldImageWidth = imageRun.Image.MeasuredImageWidth;
            float oldImageHeight = imageRun.Image.MeasuredImageHeight;
            CanvasTextLayout oldPlaceholder = Assert.IsType<CanvasTextLayout>(
                imageRun.Image.PlaceholderLayoutForTests);
            Windows.Foundation.Rect oldPlaceholderBounds = oldPlaceholder.LayoutBounds;

            IntrinsicWidthMetrics metrics = paragraph.MeasureIntrinsicWidths();

            Assert.True(metrics.Minimum > 0);
            Assert.True(metrics.Preferred >= metrics.Minimum);
            Assert.Equal(oldDesiredWidth, imageRun.DesiredWidth);
            Assert.Equal(oldDesiredHeight, imageRun.DesiredHeight);
            Assert.Equal(oldImageWidth, imageRun.Image.MeasuredImageWidth);
            Assert.Equal(oldImageHeight, imageRun.Image.MeasuredImageHeight);
            Assert.Equal(oldAutomationBounds, imageRun.Image.AutomationRenderDestination);
            Assert.Same(oldPlaceholder, imageRun.Image.PlaceholderLayoutForTests);
            Assert.Equal(oldPlaceholderBounds, oldPlaceholder.LayoutBounds);
            Assert.Equal(oldRunBounds, paragraph.GetRunRect(imageRun.InlineIndex));

            cancelRun.CancelOnNextRead();
            Assert.Throws<OperationCanceledException>(() => paragraph.MeasureIntrinsicWidths());
            context.SetCancellationToken(CancellationToken.None);

            Assert.Equal(oldDesiredWidth, imageRun.DesiredWidth);
            Assert.Equal(oldDesiredHeight, imageRun.DesiredHeight);
            Assert.Equal(oldImageWidth, imageRun.Image.MeasuredImageWidth);
            Assert.Equal(oldImageHeight, imageRun.Image.MeasuredImageHeight);
            Assert.Equal(oldAutomationBounds, imageRun.Image.AutomationRenderDestination);
            Assert.Same(oldPlaceholder, imageRun.Image.PlaceholderLayoutForTests);
            Assert.Equal(oldPlaceholderBounds, oldPlaceholder.LayoutBounds);
            Assert.Equal(oldRunBounds, paragraph.GetRunRect(imageRun.InlineIndex));
        }
        finally
        {
            paragraph.Dispose();
            await imageRun.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageDisposalInvalidatesCheckpointWithoutResurrectingNativePlaceholder(bool commit)
    {
        MarkdownLayoutContext context = CreateContext(CancellationToken.None);
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var imageRun = new InlineImageRun(
            context,
            "checkpoint image",
            "checkpoint-image.png",
            requestedWidth: new SafeHtmlLength(260f, IsPercent: false));
        paragraph.Add(imageRun);

        try
        {
            _ = paragraph.Measure(320f);
            CanvasTextLayout originalPlaceholder = Assert.IsType<CanvasTextLayout>(
                imageRun.Image.PlaceholderLayoutForTests);

            ImageBox.InlineMeasureCheckpoint checkpoint =
                imageRun.Image.BeginInlineCheckpoint();
            imageRun.Measure(150f, 20f, checkpoint);
            CanvasTextLayout replacementPlaceholder = Assert.IsType<CanvasTextLayout>(
                imageRun.Image.PlaceholderLayoutForTests);
            Assert.NotSame(originalPlaceholder, replacementPlaceholder);

            imageRun.Image.Dispose();
            if (commit)
                checkpoint.Commit();
            else
                checkpoint.Rollback();

            await imageRun.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Null(imageRun.Image.PlaceholderLayoutForTests);
        }
        finally
        {
            paragraph.Dispose();
            await imageRun.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task LaterInlineMeasureFailureRollsBackAlreadyCapturedImageStates()
    {
        MarkdownLayoutContext context = CreateContext(CancellationToken.None);
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var firstImage = new InlineImageRun(
            context,
            "first image",
            "first-image.png",
            requestedWidth: new SafeHtmlLength(260f, IsPercent: false));
        var disposedImage = new InlineImageRun(context, "second image", "second-image.png");
        paragraph.Add(firstImage);
        paragraph.Add(disposedImage);

        try
        {
            _ = paragraph.Measure(320f);
            paragraph.Arrange(12f, 24f, 320f);
            Rect oldRunBounds = paragraph.GetRunRect(firstImage.InlineIndex);
            firstImage.Image.SetInlineBounds(new Rect(
                oldRunBounds.X,
                oldRunBounds.Y,
                firstImage.DesiredWidth,
                firstImage.DesiredHeight));

            Rect oldAutomationBounds = firstImage.Image.AutomationRenderDestination;
            float oldDesiredWidth = firstImage.DesiredWidth;
            float oldImageWidth = firstImage.Image.MeasuredImageWidth;
            CanvasTextLayout oldPlaceholder = Assert.IsType<CanvasTextLayout>(
                firstImage.Image.PlaceholderLayoutForTests);
            Windows.Foundation.Rect oldPlaceholderBounds = oldPlaceholder.LayoutBounds;

            // This throws after the first image has been measured and captured,
            // exercising the partial out-state path from MeasureAtomicInlineRuns.
            disposedImage.Image.Dispose();
            Assert.Throws<ObjectDisposedException>(() => paragraph.Measure(150f));

            Assert.Equal(oldDesiredWidth, firstImage.DesiredWidth);
            Assert.Equal(oldImageWidth, firstImage.Image.MeasuredImageWidth);
            Assert.Equal(oldAutomationBounds, firstImage.Image.AutomationRenderDestination);
            Assert.Same(oldPlaceholder, firstImage.Image.PlaceholderLayoutForTests);
            Assert.Equal(oldPlaceholderBounds, oldPlaceholder.LayoutBounds);
            Assert.Equal(oldRunBounds, paragraph.GetRunRect(firstImage.InlineIndex));
        }
        finally
        {
            paragraph.Dispose();
            await Task.WhenAll(
                firstImage.Image.RetirementTask,
                disposedImage.Image.RetirementTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
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

    private sealed class CancelOnTextReadRun : InlineRun
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly string _text =
            "A long paragraph that wraps at the test width and gives the previous native layout " +
            "measurable geometry even after a replacement layout is canceled during styling.";
        private int _cancelOnNextRead;

        internal CancelOnTextReadRun(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
            RenderedLength = _text.Length;
        }

        internal void CancelOnNextRead() => Interlocked.Exchange(ref _cancelOnNextRead, 1);

        public override string Text
        {
            get
            {
                if (Interlocked.Exchange(ref _cancelOnNextRead, 0) != 0)
                    _cancellation.Cancel();
                return _text;
            }
        }
    }
}
