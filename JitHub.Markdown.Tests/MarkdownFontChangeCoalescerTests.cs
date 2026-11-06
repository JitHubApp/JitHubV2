using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace JitHub.Markdown.Tests;

/// <summary>
/// Covers how <see cref="JitHubMarkdownRuntime"/> dispatches system font changes to
/// the SVG worker. The runtime passes the coalescer its own lock and checks, under
/// that lock, that it is not shutting down and still owns the same renderer.
/// </summary>
public sealed class MarkdownFontChangeCoalescerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly object _gate = new();
    private int _published;

    [Fact]
    public async Task NotificationsWaitingForDispatchShareOnePublish()
    {
        MarkdownFontChangeCoalescer coalescer = new(_gate);
        Task first;
        Task second;
        lock (_gate)
        {
            // Holding the owner's lock keeps the dispatch from publishing, which is
            // the window in which repeated WM_FONTCHANGE messages arrive.
            first = coalescer.Request(static () => true, Publish);
            second = coalescer.Request(static () => true, Publish);
        }

        Assert.Same(first, second);
        await first.WaitAsync(Timeout);
        Assert.Equal(1, Volatile.Read(ref _published));
    }

    [Fact]
    public async Task NotificationDuringPublishGetsItsOwnDispatch()
    {
        MarkdownFontChangeCoalescer coalescer = new(_gate);
        Task? duringPublish = null;
        Task first;
        lock (_gate)
        {
            first = coalescer.Request(
                static () => true,
                () =>
                {
                    Publish();
                    if (duringPublish is null)
                    {
                        // The dispatch holds the owner's lock here, as a font change
                        // arriving on another thread would after taking it.
                        duringPublish = coalescer.Request(static () => true, Publish);
                    }
                });
        }

        await first.WaitAsync(Timeout);
        Assert.NotNull(duringPublish);
        Assert.NotSame(first, duringPublish);
        await duringPublish!.WaitAsync(Timeout);
        Assert.Equal(2, Volatile.Read(ref _published));
    }

    [Fact]
    public async Task DispatchDoesNotPublishForAnOwnerThatShutDown()
    {
        MarkdownFontChangeCoalescer coalescer = new(_gate);
        bool isCurrent = true;
        Task pending;
        lock (_gate)
        {
            pending = coalescer.Request(() => isCurrent, Publish);
            // Shutdown runs under the same lock before the dispatch gets it.
            isCurrent = false;
            coalescer.Cancel();
        }

        await pending.WaitAsync(Timeout);
        Assert.Equal(0, Volatile.Read(ref _published));
    }

    [Fact]
    public async Task NotificationAfterPublishStartsANewDispatch()
    {
        MarkdownFontChangeCoalescer coalescer = new(_gate);
        Task first;
        lock (_gate)
        {
            first = coalescer.Request(static () => true, Publish);
        }

        await first.WaitAsync(Timeout);
        Task second;
        lock (_gate)
        {
            second = coalescer.Request(static () => true, Publish);
        }

        Assert.NotSame(first, second);
        await second.WaitAsync(Timeout);
        Assert.Equal(2, Volatile.Read(ref _published));
    }

    [Fact]
    public async Task CancelLetsTheNextNotificationStartFresh()
    {
        MarkdownFontChangeCoalescer coalescer = new(_gate);
        Task first;
        Task second;
        lock (_gate)
        {
            first = coalescer.Request(static () => true, Publish);
            coalescer.Cancel();
            second = coalescer.Request(static () => true, Publish);
        }

        Assert.NotSame(first, second);
        await Task.WhenAll(first, second).WaitAsync(Timeout);
        Assert.Equal(2, Volatile.Read(ref _published));
    }

    private void Publish() => Interlocked.Increment(ref _published);
}
