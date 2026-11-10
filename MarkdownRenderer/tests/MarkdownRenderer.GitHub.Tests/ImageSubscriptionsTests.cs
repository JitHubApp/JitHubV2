using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.RenderResources;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// The control's image subscriptions forward each subscribed image's load
/// completion once, stop forwarding after they are released, and report the
/// roots of the subscribed images for relayout retries.
/// </summary>
public sealed class ImageSubscriptionsTests
{
    [Fact]
    public async Task SubscribedImageCompletionIsForwardedOnceEvenWhenSubscribedTwice()
    {
        using var fixture = new Fixture();
        ImageBox image = fixture.CreateImage(blockIndex: 3);
        try
        {
            Assert.True(fixture.Subscriptions.Subscribe(image));
            Assert.False(fixture.Subscriptions.Subscribe(image));
            Assert.True(fixture.Subscriptions.Contains(image));

            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);

            Assert.Same(image, Assert.Single(fixture.Forwarded));
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task UnsubscribeAllStopsForwardingAndForgetsEveryImage()
    {
        using var fixture = new Fixture();
        ImageBox first = fixture.CreateImage(blockIndex: 1);
        ImageBox second = fixture.CreateImage(blockIndex: 2);
        try
        {
            fixture.Subscriptions.Subscribe(first);
            fixture.Subscriptions.Subscribe(second);

            fixture.Subscriptions.UnsubscribeAll();
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(first);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(second);

            Assert.Empty(fixture.Forwarded);
            Assert.Empty(fixture.Subscriptions.Images);
            Assert.False(fixture.Subscriptions.Contains(first));
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public void RelayoutRetryCollectsTheRootOfEverySubscribedImage()
    {
        using var fixture = new Fixture();
        ImageBox[] images = [fixture.CreateImage(blockIndex: 7), fixture.CreateImage(blockIndex: 2), fixture.CreateImage(blockIndex: 7)];
        try
        {
            foreach (ImageBox image in images)
                fixture.Subscriptions.Subscribe(image);
            var pending = new SortedSet<int> { 5 };

            fixture.Subscriptions.AddBlockIndicesTo(pending);

            Assert.Equal([2, 5, 7], pending);
        }
        finally
        {
            foreach (ImageBox image in images)
                image.Dispose();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MarkdownRenderResources _renderResources = new();
        private readonly MarkdownLayoutContext _context;

        internal Fixture()
        {
            _context = DisplaySizedRasterDecodeTests.CreateContext(
                _renderResources, new BlockedResolver(), session: null, scale: 1);
            var lifetime = new MarkdownViewLifetime();
            lifetime.MarkLoaded();
            var published = new PublishedLayout();
            Subscriptions = new ImageSubscriptions(
                new RecoveryClockHost(), lifetime, published, new ImagePlanSet(lifetime, published));
            Subscriptions.LoadCompleted += (sender, args) =>
            {
                if (!args.IsBitmapOutputRecoverySignal)
                    Forwarded.Add(sender);
            };
        }

        internal ImageSubscriptions Subscriptions { get; }

        internal List<object?> Forwarded { get; } = [];

        // A failure is remembered per URL in the image resources; each image gets its own.
        internal ImageBox CreateImage(int blockIndex) =>
            new(_context, _renderResources.Images, $"https://example.test/blocked-{Guid.NewGuid():N}.png", "diagram")
            {
                BlockIndex = blockIndex,
            };

        public void Dispose() => Subscriptions.UnsubscribeAll();
    }

    private sealed class RecoveryClockHost : IImageSubscriptionsHost
    {
        public void StartRecoveryClock()
        {
        }
    }

    private sealed class BlockedResolver : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Unavailable));
    }
}
