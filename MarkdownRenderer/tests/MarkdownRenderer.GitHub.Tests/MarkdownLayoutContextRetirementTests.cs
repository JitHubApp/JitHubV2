using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownLayoutContextRetirementTests
{
    [Fact]
    public async Task PipelineDrainWaitsForLateRegisteredCleanupAndPublishesItToTheOwner()
    {
        var observedByOwner = new List<Task>();
        MarkdownLayoutContext context = CreateContext(observedByOwner.Add);
        var firstRetirement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateRetirement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        context.RegisterRetirement(firstRetirement.Task);
        Task drain = context.DrainOwnedRetirementsAsync();
        context.RegisterRetirement(lateRetirement.Task);

        Assert.Collection(
            observedByOwner,
            task => Assert.Same(firstRetirement.Task, task),
            task => Assert.Same(lateRetirement.Task, task));

        firstRetirement.SetResult(true);
        await Task.Delay(25);
        Assert.False(drain.IsCompleted);

        lateRetirement.SetResult(true);
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PipelineDrainSurfacesCleanupFailureToItsTrackedLifetime()
    {
        MarkdownLayoutContext context = CreateContext();
        var failure = new InvalidOperationException("native cleanup failed");
        context.RegisterRetirement(Task.FromException(failure));

        MarkdownLayoutRetirementException actual = await Assert.ThrowsAsync<MarkdownLayoutRetirementException>(
            () => context.DrainOwnedRetirementsAsync());

        AggregateException aggregate = Assert.IsType<AggregateException>(actual.InnerException);
        MarkdownLayoutRetirementFailureSummary summary = Assert.IsType<MarkdownLayoutRetirementFailureSummary>(
            Assert.Single(aggregate.InnerExceptions));
        Assert.Equal(failure.GetType().FullName, summary.ExceptionType);
        Assert.Equal(failure.HResult, summary.HResultCode);
        Assert.DoesNotContain(failure.Message, summary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LatePipelineCleanupFaultReachesLifetimeCapturedByAnEarlierOwnerDrain()
    {
        var owner = new SnapshotRetirementTracker();
        var lifetime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Track(lifetime.Task);
        Task shutdownDrain = owner.DrainAsync();

        MarkdownLayoutContext context = CreateContext(owner.Track);
        var firstRetirement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateRetirement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.RegisterRetirement(firstRetirement.Task);
        Task pipelineDrain = context.DrainOwnedRetirementsAsync();
        context.RegisterRetirement(lateRetirement.Task);

        Task lifetimePublication = pipelineDrain.ContinueWith(
            static (completed, state) =>
                MarkdownRebuildCoordinator.CompletePipelineLifetime(
                    completed,
                    (TaskCompletionSource)state!),
            lifetime,
            TaskScheduler.Default);

        firstRetirement.SetResult(true);
        Assert.False(pipelineDrain.IsCompleted);
        Assert.False(shutdownDrain.IsCompleted);

        lateRetirement.SetException(new InvalidOperationException("private native cleanup detail"));
        MarkdownLayoutRetirementException pipelineFailure = await Assert.ThrowsAsync<MarkdownLayoutRetirementException>(
            () => pipelineDrain.WaitAsync(TimeSpan.FromSeconds(5)));
        await lifetimePublication.WaitAsync(TimeSpan.FromSeconds(5));
        MarkdownLayoutRetirementException shutdownFailure = await Assert.ThrowsAsync<MarkdownLayoutRetirementException>(
            () => shutdownDrain.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.IsType<AggregateException>(pipelineFailure.InnerException);
        Assert.IsType<AggregateException>(shutdownFailure.InnerException);
        Assert.DoesNotContain("private native cleanup detail", shutdownFailure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PipelineDrainBoundsRetainedCleanupDiagnostics()
    {
        MarkdownLayoutContext context = CreateContext();
        var firstFailure = new InvalidOperationException("private cleanup detail 0");
        for (int index = 0; index < 40; index++)
        {
            context.RegisterRetirement(Task.FromException(
                index == 0
                    ? firstFailure
                    : new InvalidOperationException($"private cleanup detail {index}")));
        }

        MarkdownLayoutRetirementException actual = await Assert.ThrowsAsync<MarkdownLayoutRetirementException>(
            () => context.DrainOwnedRetirementsAsync());

        AggregateException aggregate = Assert.IsType<AggregateException>(actual.InnerException);
        Assert.Equal(33, aggregate.InnerExceptions.Count);
        MarkdownLayoutRetirementFailureSummary[] summaries = aggregate.InnerExceptions
            .Cast<MarkdownLayoutRetirementFailureSummary>()
            .ToArray();
        Assert.Equal(32, summaries.Count(static summary => summary.OmittedFailureCount is null));
        MarkdownLayoutRetirementFailureSummary omitted = Assert.Single(
            summaries,
            static summary => summary.OmittedFailureCount == 8);
        Assert.All(summaries, static summary =>
            Assert.DoesNotContain("private cleanup detail", summary.Message, StringComparison.Ordinal));
        Assert.Equal(firstFailure.HResult, summaries[0].HResultCode);
        Assert.Null(omitted.ExceptionType);
    }

    [Fact]
    public void RetirementFailureCanReleaseOriginalBuildExceptionAfterReporting()
    {
        var primaryFailure = new InvalidOperationException("private source path and payload");
        var cleanupSummary = new AggregateException(
            new MarkdownLayoutRetirementFailureSummary(new ObjectDisposedException("native resource")));
        var retirementFailure = new MarkdownLayoutRetirementException(cleanupSummary, primaryFailure);

        Assert.Same(primaryFailure, retirementFailure.TakePrimaryFailure());
        Assert.Null(retirementFailure.PrimaryFailure);
        Assert.Same(cleanupSummary, retirementFailure.InnerException);
        Assert.DoesNotContain("private source path and payload", retirementFailure.ToString(), StringComparison.Ordinal);
    }

    private static MarkdownLayoutContext CreateContext(Action<Task>? retirementSink = null)
    {
        var style = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
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
            FlowDirection.LeftToRight)
        {
            RetirementSink = retirementSink,
        };
    }
}
