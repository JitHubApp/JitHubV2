using System.Collections.Concurrent;
using MarkdownRenderer.Extensions;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownExtensionDeferredSceneTests
{
    [Fact]
    public void ContextCreatedOutsideAProgressiveParse_CannotDeferScenes()
    {
        var context = new MarkdownExtensionContext(new MarkdownSyntaxNode(
            MarkdownSyntaxKinds.Block.Paragraph,
            new SourceSpan(0, 1),
            literal: "x"));

        Assert.False(context.CanDeferOffscreenScenes);
        Assert.Throws<InvalidOperationException>(() => context.CreateDeferredSceneMarker());
    }

    [Fact]
    public async Task OrdinaryParse_DoesNotOfferDeferral()
    {
        var observations = new ConcurrentQueue<bool>();
        using MarkdownEngine engine = CreateEngine(context =>
        {
            observations.Enqueue(context.CanDeferOffscreenScenes);
            Assert.Throws<InvalidOperationException>(() => context.CreateDeferredSceneMarker());
        });

        await engine.ParseAsync("first\n\nsecond");

        Assert.Equal(new[] { false, false }, observations.ToArray());
    }

    [Fact]
    public async Task ProgressiveParse_OffersDeferral_WithUniqueMarkersPrefixedByTheNodeKind()
    {
        var markers = new ConcurrentQueue<string>();
        using MarkdownEngine engine = CreateEngine(context =>
        {
            Assert.True(context.CanDeferOffscreenScenes);
            markers.Enqueue(context.CreateDeferredSceneMarker());
            markers.Enqueue(context.CreateDeferredSceneMarker());
        });

        await engine.ParseForProgressivePresentationAsync("first\n\nsecond");
        await engine.ParseForProgressivePresentationAsync("third");

        Assert.Equal(6, markers.Count);
        Assert.Equal(markers.Count, markers.Distinct(StringComparer.Ordinal).Count());
        Assert.All(markers, marker =>
            Assert.StartsWith(MarkdownSyntaxKinds.Block.Paragraph + "|", marker, StringComparison.Ordinal));
    }

    private static MarkdownEngine CreateEngine(Action<MarkdownExtensionContext> observe)
        => new MarkdownEngineBuilder()
            .UseExtension(new ObservingExtension(observe))
            .Build();

    private sealed class ObservingExtension(Action<MarkdownExtensionContext> observe) : IMarkdownExtension
    {
        public string Id => "tests.deferred-scene-observer";

        public void Configure(MarkdownExtensionBuilder builder)
            => builder.RegisterBlock(
                MarkdownSyntaxKinds.Block.Paragraph,
                (context, _) => observe(context));
    }
}
