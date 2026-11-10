using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JitHub.Markdown.Lifetime;
using Xunit;

namespace JitHub.Markdown.Tests.Lifetime;

// The drains and the provider gate inside the sequence are covered by
// MarkdownViewShutdownTests; these cover what the window's close adds.
public sealed class MarkdownWindowCloseCoordinatorTests
{
    [Fact]
    public async Task DetachesNestedContentThenThePageThenRetiresTheProvidersAndClosesLast()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var steps = new List<string>();
        var coordinator = new MarkdownWindowCloseCoordinator(registry, ReportUnexpected);

        await coordinator.RetireViewsThenCloseAsync(
            timeout =>
            {
                steps.Add($"detach nested content within {timeout.TotalSeconds}s");
                return Task.CompletedTask;
            },
            timeout =>
            {
                steps.Add($"detach page within {timeout.TotalSeconds}s");
                return Task.CompletedTask;
            },
            () =>
            {
                steps.Add("retire providers");
                return Task.CompletedTask;
            },
            () => steps.Add("close"));

        Assert.Equal(
            [
                "detach nested content within 2s",
                "detach page within 2s",
                "retire providers",
                "close",
            ],
            steps);
    }

    [Fact]
    public async Task WithoutNestedContentDetachesThePageThenRetiresTheProvidersAndCloses()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var steps = new List<string>();
        var coordinator = new MarkdownWindowCloseCoordinator(registry, ReportUnexpected);

        await coordinator.RetireViewsThenCloseAsync(
            null,
            _ =>
            {
                steps.Add("detach page");
                return Task.CompletedTask;
            },
            () =>
            {
                steps.Add("retire providers");
                return Task.CompletedTask;
            },
            () => steps.Add("close"));

        Assert.Equal(["detach page", "retire providers", "close"], steps);
    }

    [Fact]
    public async Task ClosesOnlyAfterTheProvidersFinishShuttingDown()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var providersStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool closed = false;
        var coordinator = new MarkdownWindowCloseCoordinator(registry, ReportUnexpected);

        Task run = coordinator.RetireViewsThenCloseAsync(
            null,
            _ => Task.CompletedTask,
            () => providersStopped.Task,
            () => closed = true);

        Assert.False(closed);
        providersStopped.SetResult();
        await run;

        Assert.True(closed);
    }

    [Fact]
    public async Task FailedStepsAreReportedAndTheWindowStillCloses()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var pageFailure = new InvalidOperationException("Page did not detach.");
        var providerFailure = new InvalidOperationException("SVG worker did not stop.");
        var reports = new List<(Exception, string)>();
        bool closed = false;
        var coordinator = new MarkdownWindowCloseCoordinator(
            registry,
            (exception, category) => reports.Add((exception, category)));

        await coordinator.RetireViewsThenCloseAsync(
            null,
            _ => Task.FromException(pageFailure),
            () => Task.FromException(providerFailure),
            () => closed = true);

        Assert.Equal(
            [(pageFailure, "markdown-view-shutdown"), (providerFailure, "markdown-runtime-shutdown")],
            reports);
        Assert.True(closed);
    }

    private static void ReportUnexpected(Exception exception, string category) =>
        Assert.Fail($"Unexpected {category} failure: {exception}");
}
