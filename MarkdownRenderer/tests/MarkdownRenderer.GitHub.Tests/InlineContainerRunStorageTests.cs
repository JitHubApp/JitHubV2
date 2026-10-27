using System;
using System.Collections.Generic;
using System.Linq;
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

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class InlineContainerRunStorageTests
{
    [Fact]
    public async Task CapturedRunsViewStaysLiveAcrossAddsDetachAndReindex()
    {
        const string source = "before ! after tail";
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        var context = CreateContext(device, source);
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        InlineImageRun? detachedImage = null;

        IReadOnlyList<InlineRun> capturedRuns = box.Runs;
        Assert.Empty(capturedRuns);
        Assert.Same(capturedRuns, box.Runs);

        var before = new TextRun("before ")
        {
            SourceSpan = new SourceSpan(0, 7),
        };
        var image = new InlineImageRun(context, "badge", string.Empty)
        {
            SourceSpan = new SourceSpan(7, 1),
        };
        var after = new TextRun(" after")
        {
            SourceSpan = new SourceSpan(8, 6),
        };
        var tail = new TextRun(" tail")
        {
            SourceSpan = new SourceSpan(14, 5),
        };

        try
        {
            box.Add(before);
            box.Add(image);
            box.Add(after);
            box.Add(tail);

            Assert.Same(capturedRuns, box.Runs);
            Assert.Collection(
                capturedRuns,
                run => Assert.Same(before, run),
                run => Assert.Same(image, run),
                run => Assert.Same(after, run),
                run => Assert.Same(tail, run));
            Assert.Equal(new[] { 0, 1, 2, 3 }, capturedRuns.Select(run => run.InlineIndex));

            detachedImage = Assert.Single(box.DetachInlineImagesForTransfer());

            Assert.Same(image, detachedImage);
            Assert.Same(capturedRuns, box.Runs);
            Assert.Collection(
                capturedRuns,
                run => Assert.Same(before, run),
                run => Assert.Same(after, run),
                run => Assert.Same(tail, run));
            Assert.Equal(new[] { 0, 1, 2 }, capturedRuns.Select(run => run.InlineIndex));
            Assert.Equal("before  after tail", string.Concat(capturedRuns.Select(run => run.Text)));
        }
        finally
        {
            context.DiscardConstructionBlock(box);
            if (detachedImage is not null)
            {
                detachedImage.Image.Dispose();
                await detachedImage.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task PrivateRasterCloneOwnsDistinctStableRunsViewAndCopiedRuns()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture(extraTextRun: true);
        IReadOnlyList<InlineRun> sourceView = fixture.Source.Runs;
        MarkdownLayoutContext targetContext = fixture.CreateContext();

        Assert.True(fixture.Source.TryCreatePrivateRasterImageCandidates(
            targetContext,
            320f,
            out InlineContainerBox? candidate,
            out _));
        fixture.SetCandidate(targetContext, candidate);

        InlineContainerBox clone = Assert.IsType<InlineContainerBox>(candidate);
        IReadOnlyList<InlineRun> cloneView = clone.Runs;
        InlineRun[] sourceRuns = sourceView.ToArray();
        InlineRun[] clonedRuns = cloneView.ToArray();

        Assert.NotSame(sourceView, cloneView);
        Assert.Same(sourceView, fixture.Source.Runs);
        Assert.Same(cloneView, clone.Runs);
        Assert.Equal(3, sourceView.Count);
        Assert.Equal(sourceView.Count, cloneView.Count);
        Assert.All(clonedRuns, clonedRun => Assert.DoesNotContain(clonedRun, sourceRuns));
        Assert.Equal(new[] { 0, 1, 2 }, clonedRuns.Select(run => run.InlineIndex));
        Assert.IsType<InlineImageRun>(clonedRuns[1]);
    }

    private static MarkdownLayoutContext CreateContext(CanvasDevice device, string source)
    {
        var body = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
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
            device,
            theme,
            new MarkdownSourceMap(source),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }
}
