using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JitHub.Markdown.Lifetime;
using Xunit;

namespace JitHub.Markdown.Tests.Lifetime;

public sealed class MarkdownViewShutdownTests
{
    // A zero drain timeout makes "the renderer outlived the drain" immediate.
    private static readonly TimeSpan NoWait = TimeSpan.Zero;

    [Fact]
    public async Task RemovesNestedContentThenThePageAndDrainsAfterEachBeforeRetiringTheProviders()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var steps = new List<string>();
        var nestedView = new View("nested view", steps, registry);
        var pageView = new View("page view", steps, registry);
        var shutdown = new MarkdownViewShutdown(registry, NoWait, ReportUnexpected);

        await shutdown.RunAsync(
            () =>
            {
                steps.Add("detach nested content");
                nestedView.Unload();
                return Task.CompletedTask;
            },
            () =>
            {
                steps.Add("detach page");
                pageView.Unload();
                return Task.CompletedTask;
            },
            () =>
            {
                steps.Add("retire providers");
                return Task.CompletedTask;
            });

        // Each drain retires every host still registered, here the page's
        // view, which has not had its Unloaded event yet.
        Assert.Equal(
            [
                "detach nested content",
                "nested view retired",
                "page view prepared",
                "detach page",
                "retire providers",
            ],
            steps);
        Assert.Equal(0, registry.ActiveRendererCount);
        GC.KeepAlive(nestedView);
        GC.KeepAlive(pageView);
    }

    [Fact]
    public async Task WithoutNestedContentDrainsOnceAfterThePage()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var steps = new List<string>();
        var pageView = new View("page view", steps, registry);
        var shutdown = new MarkdownViewShutdown(registry, NoWait, ReportUnexpected);

        await shutdown.RunAsync(
            null,
            () =>
            {
                steps.Add("detach page");
                pageView.Unload();
                return Task.CompletedTask;
            },
            () =>
            {
                steps.Add("retire providers");
                return Task.CompletedTask;
            });

        Assert.Equal(["detach page", "page view retired", "retire providers"], steps);
        GC.KeepAlive(pageView);
    }

    [Fact]
    public async Task WaitsForTheRemovedPagesRenderersToFinishRetiring()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        registry.RecordRendererCreated();
        var retirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool providersRetired = false;
        var shutdown = new MarkdownViewShutdown(registry, TimeSpan.FromMinutes(1), ReportUnexpected);

        Task run = shutdown.RunAsync(
            null,
            () =>
            {
                // The page's renderer drains its retired resources off the UI thread.
                _ = Task.Run(async () =>
                {
                    await retirement.Task;
                    registry.RecordRendererDisposed();
                });
                return Task.CompletedTask;
            },
            () =>
            {
                providersRetired = true;
                return Task.CompletedTask;
            });

        Assert.False(run.IsCompleted);
        Assert.False(providersRetired);
        retirement.SetResult();
        await run;

        Assert.True(providersRetired);
    }

    [Fact]
    public async Task LeavesTheProvidersForProcessTeardownWhenARendererOutlivesTheDrain()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        registry.RecordRendererCreated();
        bool providersRetired = false;
        var shutdown = new MarkdownViewShutdown(registry, NoWait, ReportUnexpected);

        await shutdown.RunAsync(
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () =>
            {
                providersRetired = true;
                return Task.CompletedTask;
            });

        Assert.False(providersRetired);
        Assert.Equal(1, registry.ActiveRendererCount);
    }

    [Fact]
    public async Task LaterDrainDecidesWhenTheNestedDrainLeftARendererAlive()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        registry.RecordRendererCreated();
        bool providersRetired = false;
        var shutdown = new MarkdownViewShutdown(registry, NoWait, ReportUnexpected);

        await shutdown.RunAsync(
            () => Task.CompletedTask,
            () =>
            {
                registry.RecordRendererDisposed();
                return Task.CompletedTask;
            },
            () =>
            {
                providersRetired = true;
                return Task.CompletedTask;
            });

        Assert.True(providersRetired);
    }

    [Fact]
    public async Task NestedContentFailureIsReportedAndThePageStillRetires()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var failure = new InvalidOperationException("Nested frame did not detach.");
        var reports = new List<(Exception, string)>();
        var steps = new List<string>();
        var shutdown = new MarkdownViewShutdown(registry, NoWait, (exception, category) => reports.Add((exception, category)));

        await shutdown.RunAsync(
            () => Task.FromException(failure),
            () =>
            {
                steps.Add("detach page");
                return Task.CompletedTask;
            },
            () =>
            {
                steps.Add("retire providers");
                return Task.CompletedTask;
            });

        Assert.Equal([(failure, "markdown-shell-content-shutdown")], reports);
        Assert.Equal(["detach page", "retire providers"], steps);
    }

    [Fact]
    public async Task PageDetachFailureIsReportedAfterTheDrainAndTheProvidersStillRetire()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var failure = new InvalidOperationException("Page did not detach.");
        var reports = new List<(Exception, string)>();
        var steps = new List<string>();
        var pageView = new View("page view", steps, registry);
        var shutdown = new MarkdownViewShutdown(registry, NoWait, (exception, category) => reports.Add((exception, category)));

        await shutdown.RunAsync(
            null,
            () => Task.FromException(failure),
            () =>
            {
                steps.Add("retire providers");
                return Task.CompletedTask;
            });

        Assert.Equal([(failure, "markdown-view-shutdown")], reports);
        Assert.Equal(["page view prepared", "retire providers"], steps);
        GC.KeepAlive(pageView);
    }

    [Fact]
    public async Task HostThatCannotReleaseItsRendererIsReportedAndKeepsTheProvidersAlive()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var failure = new InvalidOperationException("Teardown failed.");
        var reports = new List<(Exception, string)>();
        var stuck = new View("stuck view", [], registry, failure);
        bool providersRetired = false;
        var shutdown = new MarkdownViewShutdown(registry, NoWait, (exception, category) => reports.Add((exception, category)));

        await shutdown.RunAsync(
            null,
            () => Task.CompletedTask,
            () =>
            {
                providersRetired = true;
                return Task.CompletedTask;
            });

        (Exception reported, string category) = Assert.Single(reports);
        Assert.Equal("markdown-renderer-shutdown", category);
        Assert.Same(failure, Assert.Single(Assert.IsType<AggregateException>(reported).InnerExceptions));
        Assert.False(providersRetired);
        GC.KeepAlive(stuck);
    }

    [Fact]
    public async Task ProviderShutdownFailureIsReported()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var failure = new InvalidOperationException("SVG worker did not stop.");
        var reports = new List<(Exception, string)>();
        var shutdown = new MarkdownViewShutdown(registry, NoWait, (exception, category) => reports.Add((exception, category)));

        await shutdown.RunAsync(null, () => Task.CompletedTask, () => Task.FromException(failure));

        Assert.Equal([(failure, "markdown-runtime-shutdown")], reports);
    }

    private static void ReportUnexpected(Exception exception, string category) =>
        Assert.Fail($"Unexpected {category} failure: {exception}");

    // A Markdown view that owns one renderer. Unloading it retires the renderer;
    // a drain retires it early through the ledger, as application shutdown does.
    private sealed class View : IMarkdownRendererShutdownParticipant
    {
        private readonly string _name;
        private readonly List<string> _steps;
        private readonly MarkdownRendererLifetimeRegistry _registry;
        private readonly Exception? _failure;
        private bool _retired;

        public View(string name, List<string> steps, MarkdownRendererLifetimeRegistry registry, Exception? failure = null)
        {
            _name = name;
            _steps = steps;
            _registry = registry;
            _failure = failure;
            registry.Register(this);
            registry.RecordRendererCreated();
        }

        public void Unload()
        {
            if (_retired)
                return;

            _retired = true;
            _steps.Add(_name + " retired");
            _registry.RecordRendererDisposed();
        }

        public void PrepareForApplicationShutdown()
        {
            if (_failure is not null)
                throw _failure;

            if (_retired)
                return;

            _retired = true;
            _steps.Add(_name + " prepared");
            _registry.RecordRendererDisposed();
        }
    }
}
