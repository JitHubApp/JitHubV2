using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
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

public sealed class ExceptionSafeContainerRetirementTests
{
    [Fact]
    public void NestedStackDisposalAttemptsAllSiblingsAndPreservesFailures()
    {
        var firstFailure = new InvalidOperationException("first child cleanup");
        var secondFailure = new ObjectDisposedException("second child");
        var nested = new StackBox();
        var outer = new StackBox();
        var first = new ProbeBox { DisposeFailure = firstFailure };
        var nestedTrailing = new ProbeBox();
        var second = new ProbeBox { DisposeFailure = secondFailure };
        var outerTrailing = new ProbeBox();

        nested.Add(first);
        nested.Add(nestedTrailing);
        outer.Add(nested);
        outer.Add(second);
        outer.Add(outerTrailing);

        AggregateException failure = Assert.Throws<AggregateException>(outer.Dispose);

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, nestedTrailing.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(1, outerTrailing.DisposeCount);
        IReadOnlyList<Exception> errors = failure.Flatten().InnerExceptions;
        Assert.Contains(firstFailure, errors);
        Assert.Contains(secondFailure, errors);
    }

    [Fact]
    public void StackRetirementCollectionAttemptsTrailingSubtreeAndRetainsItsBarriers()
    {
        var firstFailure = new InvalidOperationException("first collector failure");
        var secondFailure = new InvalidOperationException("second collector failure");
        var nested = new StackBox();
        var outer = new StackBox();
        var first = new ProbeBox { CollectFailure = firstFailure };
        var nestedTrailing = new ProbeBox();
        var second = new ProbeBox { CollectFailure = secondFailure };
        var outerTrailing = new ProbeBox();
        nested.Add(first);
        nested.Add(nestedTrailing);
        outer.Add(nested);
        outer.Add(second);
        outer.Add(outerTrailing);
        var pending = new List<Task>();

        AggregateException failure = Assert.Throws<AggregateException>(
            () => outer.CollectRetirementTasks(pending));

        Assert.Equal(1, first.CollectCount);
        Assert.Equal(1, nestedTrailing.CollectCount);
        Assert.Equal(1, second.CollectCount);
        Assert.Equal(1, outerTrailing.CollectCount);
        Assert.Equal(4, pending.Count);
        Assert.All(pending, task => Assert.True(task.IsCompletedSuccessfully));
        Assert.Contains(firstFailure, failure.Flatten().InnerExceptions);
        Assert.Contains(secondFailure, failure.Flatten().InnerExceptions);
    }

    [Fact]
    public void ListItemDisposalStillRetiresContentAfterNestedChildFailure()
    {
        MarkdownLayoutContext context = CreateContext();
        var marker = new InlineContainerBox(context, MarkdownElementKeys.ListMarker);
        var content = new StackBox();
        var first = new ProbeBox { DisposeFailure = new InvalidOperationException("content failure") };
        var trailing = new ProbeBox();
        content.Add(first);
        content.Add(trailing);
        var item = new ListItemBox(marker, content, markerWidth: 20);

        Assert.Throws<InvalidOperationException>(item.Dispose);

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, trailing.DisposeCount);
    }

    [Fact]
    public void ListItemRetirementCollectionAttemptsTrailingContentAfterNestedFailure()
    {
        MarkdownLayoutContext context = CreateContext();
        var marker = new InlineContainerBox(context, MarkdownElementKeys.ListMarker);
        var content = new StackBox();
        var first = new ProbeBox { CollectFailure = new InvalidOperationException("content collector failure") };
        var trailing = new ProbeBox();
        content.Add(first);
        content.Add(trailing);
        var item = new ListItemBox(marker, content, markerWidth: 20);
        var pending = new List<Task>();

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => item.CollectRetirementTasks(pending));

        Assert.Equal(1, first.CollectCount);
        Assert.Equal(1, trailing.CollectCount);
        Assert.Same(first.CollectFailure, failure);
        Assert.Contains(Task.CompletedTask, pending);
    }

    [Fact]
    public void CleanupFailureDetailsAreBoundedWithoutSkippingAnyOwnedChild()
    {
        const int childCount = 100;
        var stack = new StackBox();
        var children = new ProbeBox[childCount];
        for (int index = 0; index < childCount; index++)
        {
            children[index] = new ProbeBox
            {
                DisposeFailure = new InvalidOperationException($"failure {index}"),
            };
            stack.Add(children[index]);
        }

        AggregateException failure = Assert.Throws<AggregateException>(stack.Dispose);

        Assert.All(children, child => Assert.Equal(1, child.DisposeCount));
        Assert.Equal(9, failure.InnerExceptions.Count);
        Assert.Contains("omitted", failure.InnerExceptions[^1].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeepAggregateFailureIsBoundedAndDoesNotPreventTrailingCleanup()
    {
        Exception deepFailure = new InvalidOperationException("known leaf");
        for (int depth = 0; depth < 128; depth++)
            deepFailure = new AggregateException(deepFailure);

        var stack = new StackBox();
        var first = new ProbeBox { DisposeFailure = deepFailure };
        var trailing = new ProbeBox();
        stack.Add(first);
        stack.Add(trailing);

        AggregateException failure = Assert.Throws<AggregateException>(stack.Dispose);

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, trailing.DisposeCount);
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Contains("depth limit", failure.InnerExceptions[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TableAndInlineRetirementVisitEveryCellOwnedImage()
    {
        MarkdownLayoutContext context = CreateContext();
        var firstCell = new InlineContainerBox(context, MarkdownElementKeys.TableHeader)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var secondCell = new InlineContainerBox(context, MarkdownElementKeys.TableCell)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var firstImage = new InlineImageRun(context, "first", "https://example.test/first.png");
        var secondImage = new InlineImageRun(context, "second", "https://example.test/second.png");
        firstCell.Add(firstImage);
        secondCell.Add(secondImage);
        var table = new TableBox(
            context,
            new[] { new[] { firstCell } },
            new[] { new[] { secondCell } });

        var pending = new List<Task>();
        table.CollectRetirementTasks(pending);
        await Task.WhenAll(pending);
        await Task.WhenAll(firstImage.Image.RetirementTask, secondImage.Image.RetirementTask);

        Assert.True(firstImage.Image.RetirementTask.IsCompletedSuccessfully);
        Assert.True(secondImage.Image.RetirementTask.IsCompletedSuccessfully);
    }

    private static MarkdownLayoutContext CreateContext()
    {
        var body = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16,
            Foreground = Color.FromArgb(255, 24, 24, 24),
        };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.ListMarker] = body,
            [MarkdownElementKeys.Table] = body,
            [MarkdownElementKeys.TableHeader] = body,
            [MarkdownElementKeys.TableCell] = body,
            [MarkdownElementKeys.Link] = body,
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
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }

    private class ProbeBox : BlockBox
    {
        internal int DisposeCount { get; private set; }
        internal int CollectCount { get; private set; }
        internal Exception? DisposeFailure { get; init; }
        internal Exception? CollectFailure { get; init; }

        public override float Measure(float availableWidth) => 0;
        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }

        public override void Dispose()
        {
            DisposeCount++;
            if (DisposeFailure is { } failure)
                throw failure;
        }

        internal override void CollectRetirementTasks(List<Task> pendingTasks)
        {
            CollectCount++;
            pendingTasks.Add(Task.CompletedTask);
            if (CollectFailure is { } failure)
                throw failure;
        }
    }

}
