using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JitHub.Markdown.Tests;

public sealed class MarkdownRendererLifetimeRegistryTests
{
    [Fact]
    public async Task WaitForRendererDisposal_CompletesImmediatelyWithoutActiveRenderers()
    {
        var registry = new MarkdownRendererLifetimeRegistry(new FakeTimeProvider());

        Assert.True(await registry.WaitForRendererDisposalAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task WaitForRendererDisposal_CompletesWhenTheLastRendererRetires()
    {
        var registry = new MarkdownRendererLifetimeRegistry(new FakeTimeProvider());
        registry.RecordRendererCreated();
        registry.RecordRendererCreated();

        Task<bool> wait = registry.WaitForRendererDisposalAsync(TimeSpan.FromSeconds(2));
        registry.RecordRendererDisposed();
        Assert.False(wait.IsCompleted);
        registry.RecordRendererDisposed();

        Assert.True(await wait);
        Assert.Equal(0, registry.ActiveRendererCount);
    }

    [Fact]
    public async Task WaitForRendererDisposal_TimesOutWhileARendererIsStillDraining()
    {
        var time = new FakeTimeProvider();
        var registry = new MarkdownRendererLifetimeRegistry(time);
        registry.RecordRendererCreated();

        Task<bool> wait = registry.WaitForRendererDisposalAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.False(await wait);
        Assert.Equal(1, registry.ActiveRendererCount);
    }

    [Fact]
    public async Task RecordRendererCreated_RearmsTheDisposalSignalAfterTheCountReturnsToZero()
    {
        var time = new FakeTimeProvider();
        var registry = new MarkdownRendererLifetimeRegistry(time);
        registry.RecordRendererCreated();
        registry.RecordRendererDisposed();

        registry.RecordRendererCreated();
        Task<bool> wait = registry.WaitForRendererDisposalAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.False(await wait);
    }

    [Fact]
    public void RecordRendererDisposed_NeverDropsBelowZero()
    {
        var registry = new MarkdownRendererLifetimeRegistry(new FakeTimeProvider());

        registry.RecordRendererDisposed();
        registry.RecordRendererCreated();

        Assert.Equal(1, registry.ActiveRendererCount);
    }

    [Fact]
    public void PrepareAllForApplicationShutdown_RetiresEveryHostAndReportsReleasedRenderers()
    {
        var registry = new MarkdownRendererLifetimeRegistry(new FakeTimeProvider());
        var order = new List<string>();
        var first = new Participant("first", order, registry);
        var second = new Participant("second", order, registry);
        Assert.False(registry.Register(first));
        Assert.False(registry.Register(second));
        registry.RecordRendererCreated();
        registry.RecordRendererCreated();

        int released = registry.PrepareAllForApplicationShutdown();

        Assert.Equal(["second", "first"], order);
        Assert.Equal(2, released);
        Assert.Equal(0, registry.ActiveRendererCount);
        GC.KeepAlive(first);
        GC.KeepAlive(second);
    }

    [Fact]
    public void PrepareAllForApplicationShutdown_AttemptsEveryHostBeforeReportingFailures()
    {
        var registry = new MarkdownRendererLifetimeRegistry(new FakeTimeProvider());
        var order = new List<string>();
        var failure = new InvalidOperationException("Teardown failed.");
        var failing = new Participant("failing", order, registry, failure);
        var healthy = new Participant("healthy", order, registry);
        registry.Register(failing);
        registry.Register(healthy);
        registry.RecordRendererCreated();
        registry.RecordRendererCreated();

        AggregateException aggregate = Assert.Throws<AggregateException>(
            () => registry.PrepareAllForApplicationShutdown());

        Assert.StartsWith(
            "One or more Markdown viewers could not release their renderer during application shutdown.",
            aggregate.Message,
            StringComparison.Ordinal);
        Assert.Same(failure, Assert.Single(aggregate.InnerExceptions));
        Assert.Equal(["healthy", "failing"], order);
        Assert.Equal(1, registry.ActiveRendererCount);
        GC.KeepAlive(failing);
        GC.KeepAlive(healthy);
    }

    [Fact]
    public void Register_AfterShutdownBeganTellsTheHostNotToCreateRenderers()
    {
        var registry = new MarkdownRendererLifetimeRegistry(new FakeTimeProvider());
        registry.PrepareAllForApplicationShutdown();

        var late = new Participant("late", [], registry);

        Assert.True(registry.Register(late));
        GC.KeepAlive(late);
    }

    private sealed class Participant : IMarkdownRendererShutdownParticipant
    {
        private readonly string _name;
        private readonly List<string> _order;
        private readonly MarkdownRendererLifetimeRegistry _registry;
        private readonly Exception? _failure;

        public Participant(
            string name,
            List<string> order,
            MarkdownRendererLifetimeRegistry registry,
            Exception? failure = null)
        {
            _name = name;
            _order = order;
            _registry = registry;
            _failure = failure;
        }

        public void PrepareForApplicationShutdown()
        {
            _order.Add(_name);
            if (_failure is not null)
                throw _failure;

            _registry.RecordRendererDisposed();
        }
    }
}
