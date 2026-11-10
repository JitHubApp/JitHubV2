using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Controls.Threading;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// An image load completion queues a relayout of its own root only while the
/// image is still subscribed, before and after its UI Automation notice; other
/// completions rebuild, repaint or retry bitmap output. Off-thread completions
/// reach the UI thread through a post, and a rejected post is recorded for retry.
/// </summary>
public sealed class ImageLoadCompletionTests
{
    [Fact]
    public void SubscribedImageWhoseLayoutChangedQueuesARelayoutOfItsRoot()
    {
        using var fixture = new ImageRelayoutFixture();
        var host = new CompletionHost();
        ImageLoadCompletion completion = CreateCompletion(fixture, host);
        ImageBox image = fixture.SubscribeImage(blockIndex: 2);

        completion.OnLoadCompleted(image, new LoadCompletedEventArgs(layoutInvalidated: true));

        Assert.Same(image, Assert.Single(host.StatusNotices));
        Assert.True(fixture.Relayout.HasPendingWork);
        Assert.Equal([MarkdownUiPriority.Low], fixture.Scheduler.Posted);
        Assert.Equal(0, host.FullRebuilds);
    }

    [Fact]
    public void CompletionOfAnImageThatIsNotSubscribedIsIgnored()
    {
        using var fixture = new ImageRelayoutFixture();
        var host = new CompletionHost();
        ImageLoadCompletion completion = CreateCompletion(fixture, host);
        ImageBox image = fixture.SubscribeImage(blockIndex: 1);
        fixture.Subscriptions.UnsubscribeAll();

        completion.OnLoadCompleted(image, new LoadCompletedEventArgs(layoutInvalidated: true));

        Assert.Empty(host.StatusNotices);
        Assert.False(fixture.Relayout.HasPendingWork);
        Assert.Equal(0, host.Repaints);
    }

    [Fact]
    public void ImageUnsubscribedByItsAutomationNoticeIsNotQueued()
    {
        using var fixture = new ImageRelayoutFixture();
        var host = new CompletionHost();
        host.OnStatusNotice = fixture.Subscriptions.UnsubscribeAll;
        ImageLoadCompletion completion = CreateCompletion(fixture, host);
        ImageBox image = fixture.SubscribeImage(blockIndex: 3);

        completion.OnLoadCompleted(image, new LoadCompletedEventArgs(layoutInvalidated: true));

        Assert.Single(host.StatusNotices);
        Assert.False(fixture.Relayout.HasPendingWork);
        Assert.Empty(fixture.Scheduler.Posted);
    }

    [Fact]
    public void PaintOnlyCompletionRepaintsWithoutRelayout()
    {
        using var fixture = new ImageRelayoutFixture();
        var host = new CompletionHost();
        ImageLoadCompletion completion = CreateCompletion(fixture, host);
        ImageBox image = fixture.SubscribeImage(blockIndex: 0);

        completion.OnLoadCompleted(image, new LoadCompletedEventArgs(layoutInvalidated: false));

        Assert.Equal(1, host.Repaints);
        Assert.False(fixture.Relayout.HasPendingWork);
    }

    [Fact]
    public void LayoutCompletionFromASenderThatIsNotAnImageRebuilds()
    {
        using var fixture = new ImageRelayoutFixture();
        var host = new CompletionHost();
        ImageLoadCompletion completion = CreateCompletion(fixture, host);

        completion.OnLoadCompleted(new object(), new LoadCompletedEventArgs(layoutInvalidated: true));

        Assert.Equal(1, host.FullRebuilds);
        Assert.False(fixture.Relayout.HasPendingWork);
    }

    [Fact]
    public void OffThreadCompletionIsPostedToTheUiThread()
    {
        using var fixture = new ImageRelayoutFixture();
        var host = new CompletionHost();
        ImageLoadCompletion completion = CreateCompletion(fixture, host);
        ImageBox image = fixture.SubscribeImage(blockIndex: 2);
        fixture.Scheduler.OnUiThread = false;

        completion.OnLoadCompleted(image, new LoadCompletedEventArgs(layoutInvalidated: true));

        Assert.Equal([MarkdownUiPriority.Normal], fixture.Scheduler.Posted);
        Assert.Empty(host.StatusNotices);
        fixture.Scheduler.OnUiThread = true;
        fixture.Scheduler.RunPending();
        Assert.Single(host.StatusNotices);
        Assert.True(fixture.Relayout.HasPendingWork);
    }

    [Fact]
    public void RejectedOffThreadCompletionIsRecordedForARebuildRetry()
    {
        using var fixture = new ImageRelayoutFixture();
        var host = new CompletionHost();
        ImageLoadCompletion completion = CreateCompletion(fixture, host);
        ImageBox image = fixture.SubscribeImage(blockIndex: 2);
        fixture.Scheduler.OnUiThread = false;
        fixture.Scheduler.RejectPosts = true;

        completion.OnLoadCompleted(image, new LoadCompletedEventArgs(layoutInvalidated: true));

        Assert.Equal(1, host.RejectedCrossThreadRebuilds);
        Assert.Empty(host.StatusNotices);
    }

    [Fact]
    public void CompletionWithoutAUiSchedulerIsIgnored()
    {
        using var fixture = new ImageRelayoutFixture();
        var host = new CompletionHost();
        var completion = new ImageLoadCompletion(
            host, fixture.Lifetime, fixture.Published, fixture.LayoutCommit, fixture.Subscriptions,
            fixture.Relayout, scheduler: null);
        ImageBox image = fixture.SubscribeImage(blockIndex: 2);

        completion.OnLoadCompleted(image, new LoadCompletedEventArgs(layoutInvalidated: true));

        Assert.Empty(host.StatusNotices);
        Assert.False(fixture.Relayout.HasPendingWork);
    }

    private static ImageLoadCompletion CreateCompletion(ImageRelayoutFixture fixture, CompletionHost host) =>
        new(host, fixture.Lifetime, fixture.Published, fixture.LayoutCommit, fixture.Subscriptions,
            fixture.Relayout, fixture.Scheduler);

    private sealed class CompletionHost : IImageLoadCompletionHost
    {
        internal List<ImageBox> StatusNotices { get; } = [];

        internal Action? OnStatusNotice { get; set; }

        internal int FullRebuilds { get; private set; }

        internal int RejectedCrossThreadRebuilds { get; private set; }

        internal int Repaints { get; private set; }

        public IMarkdownPerformanceSession? PerformanceSession => null;

        public float LayoutWidth => 100;

        public bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
            snapshot?.GeometryRecoveryRequired == true;

        public void NotifyImageStatusChanged(ImageBox image)
        {
            StatusNotices.Add(image);
            OnStatusNotice?.Invoke();
        }

        public void RequestFullRebuild() => FullRebuilds++;

        public void RecordRejectedCrossThreadFullRebuild() => RejectedCrossThreadRebuilds++;

        public void InvalidatePaint() => Repaints++;
    }
}
