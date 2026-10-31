using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubMarkdownImageResolverLeaseTests
{
    [Fact]
    public void ResolverProviderKeepsOneFacadeIdentityAcrossConcurrentViews()
    {
        var legacy = new FakeResolver();
        int factoryCalls = 0;
        var provider = new GitHubMarkdownImageResolverProvider(
            legacy,
            supportsSourceLeases: true,
            leasedResolverFactory: () =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new LeasedGitHubMarkdownImageResolver(
                    legacy,
                    static (_, _, _, _) => throw new InvalidOperationException(
                        "This identity test must not resolve image bytes."));
            });
        var views = new IMarkdownImageResolver[64];

        Assert.Equal(0, factoryCalls);
        Parallel.For(0, views.Length, index => views[index] = provider.CreateMarkdownImageResolver());

        Assert.Equal(1, factoryCalls);
        Assert.All(views, view => Assert.Same(views[0], view));
        Assert.Same(views[0], provider.CreateMarkdownImageResolver());
    }

    [Fact]
    public void ResolverProviderPreservesLegacyResolverAndDoesNotAdvertiseLeases()
    {
        var legacy = new FakeResolver();
        int leasedFactoryCalls = 0;
        var provider = new GitHubMarkdownImageResolverProvider(
            legacy,
            supportsSourceLeases: false,
            leasedResolverFactory: () =>
            {
                leasedFactoryCalls++;
                throw new InvalidOperationException("The legacy resolver must not request the leased adapter.");
            });

        IMarkdownImageResolver resolver = provider.CreateMarkdownImageResolver();

        Assert.Same(legacy, resolver);
        Assert.IsNotAssignableFrom<IMarkdownLeasedImageResolver>(resolver);
        Assert.Equal(0, leasedFactoryCalls);
    }

    [Fact]
    public async Task LeasedResolverAdapterAddsOwnershipWithoutChangingLegacyOperations()
    {
        var legacy = new FakeResolver();
        var owner = new TrackingOwner();
        byte[] bytes = [1, 2, 3];
        var expectedLease = new MarkdownImageResolutionLease(
            MarkdownImageResolution.Resolved(new MarkdownImageAsset(bytes, "image/png")),
            owner);
        var provider = new GitHubMarkdownImageResolverProvider(
            legacy,
            supportsSourceLeases: true,
            leasedResolverFactory: () => new LeasedGitHubMarkdownImageResolver(
                legacy,
                (_, _, _, _) => ValueTask.FromResult(expectedLease)));

        IMarkdownImageResolver result = provider.CreateMarkdownImageResolver();
        var leased = Assert.IsAssignableFrom<IMarkdownLeasedImageResolver>(result);
        Assert.IsAssignableFrom<IMarkdownImageSourceByteAdmittedResolver>(result);
        Assert.IsAssignableFrom<IMarkdownImagePrefetcher>(result);

        MarkdownImageResolution legacyResolution = await result.ResolveAsync(
            "legacy-source", new MarkdownImageResolveContext(null), CancellationToken.None);
        Assert.Same(legacy.Resolution.Asset, legacyResolution.Asset);

        using var admission = new FakeAdmission();
        MarkdownImageResolution admittedResolution = await ((IMarkdownImageSourceByteAdmittedResolver)result)
            .ResolveWithSourceByteAdmissionAsync(
                "admitted-source", new MarkdownImageResolveContext(null), admission, CancellationToken.None);
        Assert.Same(legacy.Resolution.Asset, admittedResolution.Asset);
        await ((IMarkdownImagePrefetcher)result).PrefetchAsync(
            ["prefetch"], new MarkdownImageResolveContext(null), CancellationToken.None);
        MarkdownImageResolutionLease actual = await leased.ResolveWithSourceLeaseAsync(
            "leased-source", new MarkdownImageResolveContext(null), admission, CancellationToken.None);

        Assert.Same(expectedLease, actual);
        Assert.Equal(1, legacy.AdmittedResolveCalls);
        Assert.Equal(1, legacy.PrefetchCalls);
        Assert.Equal(0, owner.DisposeCount);
        actual.Dispose();
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public void CandidateTransfersOwnershipAndClearsItsBorrowedResolution()
    {
        byte[] bytes = [4, 5, 6];
        var owner = new TrackingOwner();
        var candidate = GitHubMarkdownImageCandidate.Resolved(
            new MarkdownImageAsset(bytes, "image/png"), owner);

        MarkdownImageResolutionLease lease = candidate.DetachLease();

        Assert.Same(bytes, lease.Resolution.Asset!.Bytes);
        Assert.Equal(0, owner.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => candidate.Resolution);
        lease.Dispose();
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public void AbandonedCandidateClearsBytesBeforeReleasingItsOwner()
    {
        byte[] bytes = [7, 8, 9];
        var owner = new TrackingOwner();
        var candidate = GitHubMarkdownImageCandidate.Resolved(
            new MarkdownImageAsset(bytes, "image/png"), owner);

        candidate.Dispose();

        Assert.Throws<ObjectDisposedException>(() => candidate.Resolution);
        Assert.Equal(1, owner.DisposeCount);
        candidate.Dispose();
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task LfsPointerReservationIsReleasedBeforeMediaFetchAndLeaseTransfersWithoutCopy()
    {
        byte[] bytes = [10, 11, 12, 13];
        var pointerReservation = new TrackingOwner();
        var sourceReservation = new TrackingOwner();
        GitHubImageSourceLease? sourceLease = null;

        GitHubImageDownload? download = await GitHubLeasedImageDownloadTransfer.ResolveLfsDownloadAsync(
            pointerReservation,
            () =>
            {
                Assert.Equal(1, pointerReservation.DisposeCount);
                sourceLease = new GitHubImageSourceLease(
                    new GitHubCachedImage("lfs-source", bytes, "image/png", IsFromCache: false),
                    sourceReservation);
                return Task.FromResult<GitHubImageSourceLease?>(sourceLease);
            });

        GitHubImageDownload actualDownload = Assert.IsType<GitHubImageDownload>(download);
        Assert.Same(bytes, actualDownload.Bytes);
        Assert.Equal(1, pointerReservation.DisposeCount);
        Assert.Same(sourceLease, actualDownload.SourceByteLease);
        Assert.Equal(0, sourceReservation.DisposeCount);
        actualDownload.SourceByteLease!.Dispose();
        Assert.Equal(1, sourceReservation.DisposeCount);
    }

    private sealed class FakeResolver :
        IMarkdownImageResolver,
        IMarkdownImageSourceByteAdmittedResolver,
        IMarkdownImagePrefetcher
    {
        internal MarkdownImageResolution Resolution { get; } = MarkdownImageResolution.Resolved(
            new MarkdownImageAsset([21, 22], "image/png"));

        internal int PrefetchCalls { get; private set; }
        internal int AdmittedResolveCalls { get; private set; }

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult(Resolution);

        public ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            AdmittedResolveCalls++;
            return ValueTask.FromResult(Resolution);
        }

        public ValueTask PrefetchAsync(
            IReadOnlyList<string> sources,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            PrefetchCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAdmission : IMarkdownImageSourceByteAdmission, IDisposable
    {
        public bool IsSpeculative => false;
        public long MaximumReservationBytes => 1024;
        public ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IDisposable>(new TrackingOwner());

        public void Dispose()
        {
        }
    }

    private sealed class TrackingOwner : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
