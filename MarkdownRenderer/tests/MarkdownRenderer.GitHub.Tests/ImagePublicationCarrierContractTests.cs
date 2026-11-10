using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.Loading;
using MarkdownRenderer.Layout.Internal.Images.Svg;
using MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.SvgPublicationCommitTestOperations;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ImagePublicationCarrierContractTests
{
    [Fact]
    public async Task SvgPublicationFactoryAcceptsOnlyWeakSourceIdentityAndScalarLength()
    {
        await using var fixture = new QueuedSvgPublicationFixture();
        var (metadata, source) = fixture.CreateMetadata(
            new PublicationCommitSvgDocument().Info, "weak-source", sourceLength: 4096);

        RetentionCollector.CollectUntilDead(source);

        // The metadata keeps the scalar length for tracing but never the bytes.
        Assert.False(source.IsAlive, "Publication metadata must not keep its source array alive.");
        Assert.False(metadata.SourceIdentity.TryGetTarget(out _));
        Assert.Equal(4096, metadata.SourceByteLength);
        GC.KeepAlive(metadata);
    }

    [Fact]
    public async Task OpenedRenderHandoffRetainsItsDocumentButNotItsSource()
    {
        await using var fixture = new QueuedSvgPublicationFixture();
        var (document, info, candidateUse, disposed) = fixture.OpenCandidateDocument();
        var (metadata, source) = fixture.CreateMetadata(info, "opened-handoff");
        SvgOpenedRenderWork work = CreateOpenedWork(fixture, metadata, document, candidateUse);

        RetentionCollector.CollectUntilDead(source);

        // Between Open and Render the handoff owns the document it will
        // render, never the source bytes the document was opened from.
        Assert.False(source.IsAlive, "The opened-render handoff must not keep its source array alive.");
        Assert.True(document.IsAlive);
        PublicationCommitSvgDocument opened = Assert.IsType<PublicationCommitSvgDocument>(document.Target);
        Assert.Same(opened, work.Document);
        Assert.Same(candidateUse, work.DocumentUse);
        Assert.Same(metadata, work.Metadata);
        Assert.Equal("content:opened-handoff", work.ContentCacheKey);
        GC.KeepAlive(work);

        // An unconsumed handoff retires its document and work exactly once.
        work.Dispose();
        await disposed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(work.Document);
        Assert.Null(work.DocumentUse);
        work.Dispose();
        Assert.Equal(1, opened.DisposeCount);
    }

    [Fact]
    public void PendingRenderDisposalPreventsConsumptionAndRetiresOnce()
    {
        SvgOpenedRenderOwnership ownership = default;
        Assert.False(ownership.TryCompleteConsumed());
        Assert.True(ownership.TryDisposePending());
        Assert.False(ownership.TryDisposePending());
        Assert.False(ownership.TryConsume());
        Assert.False(ownership.TryCompleteConsumed());
    }

    [Fact]
    public void ConsumedRenderCannotBeDisposedBeforeActualCompletion()
    {
        SvgOpenedRenderOwnership ownership = default;
        Assert.True(ownership.TryConsume());
        Assert.False(ownership.TryConsume());
        Assert.False(ownership.TryDisposePending());
        Assert.True(ownership.TryCompleteConsumed());
        Assert.False(ownership.TryCompleteConsumed());
        Assert.False(ownership.TryDisposePending());
        Assert.False(ownership.TryConsume());
    }

    [Theory]
    [InlineData(nameof(SvgPublicationCommit.CreateTiledPublication))]
    [InlineData(nameof(SvgPublicationCommit.CreateBitmapPublication))]
    public async Task SvgPublicationReturnsAnAllocationFreeCallbackPair(string methodName)
    {
        await using var fixture = new QueuedSvgPublicationFixture();
        var document = new PublicationCommitSvgDocument();
        var (metadata, _) = fixture.CreateMetadata(document.Info, "callback-pair");
        (Action Publish, Action? OnDropped) publication = methodName switch
        {
            nameof(SvgPublicationCommit.CreateTiledPublication) =>
                fixture.Image.SvgPublication.CreateTiledPublication(
                    metadata, document, (SvgDocumentUseLease)AcquireCandidate(fixture.Image, document)),
            nameof(SvgPublicationCommit.CreateBitmapPublication) =>
                fixture.Image.SvgPublication.CreateBitmapPublication(
                    metadata, new SharedCanvasBitmapCache.Lease()),
            _ => throw new ArgumentOutOfRangeException(nameof(methodName)),
        };

        // The pair is a built-in value tuple of exactly two callbacks: no
        // separate carrier object is allocated beside the publication owner.
        object carrier = publication;
        Assert.IsType<ValueTuple<Action, Action>>(carrier);
        Assert.NotNull(publication.Publish);
        Action onDropped = Assert.IsType<Action>(publication.OnDropped);

        // Drop the unpublished pair so its candidate or lease is released.
        onDropped();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SvgOpenedRenderWork CreateOpenedWork(
        QueuedSvgPublicationFixture fixture,
        SvgPublicationMetadata metadata,
        WeakReference document,
        SvgDocumentUseLease candidateUse)
    {
        Assert.True(fixture.Image.Lifetime.TryBeginWork(out ImageWorkLease? imageWork));
        return new SvgOpenedRenderWork(
            fixture.Image.SourceLoadHost,
            fixture.Image.Resources,
            metadata,
            "content:opened-handoff",
            builtInSourceAliasKey: null,
            usesTiles: false,
            Assert.IsAssignableFrom<IMarkdownSvgDocument>(document.Target),
            candidateUse,
            ownerSlot: null,
            Assert.IsType<ImageWorkLease>(imageWork),
            preparationCancellation: null,
            CancellationToken.None,
            CancellationToken.None,
            openAdmissionEntry: null,
            providerAdmissionHeld: false);
    }
}
