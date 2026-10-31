using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MarkdownImageResolverAdmissionTests
{
    [Fact]
    public async Task LifecycleWrapperPreservesAdmittedResolution()
    {
        var inner = new RecordingAdmittedResolver();
        var lifecycle = new MarkdownLifecycleImageResolver(inner);
        var admission = new CountingAdmission();
        var context = new MarkdownImageResolveContext(new Uri("https://github.com/example/repo/"));

        MarkdownImageResolution result = await lifecycle.ResolveWithSourceByteAdmissionAsync(
            "docs/image.png", context, admission, CancellationToken.None);

        Assert.True(result.IsHandled);
        Assert.Equal([1, 2, 3], result.Asset!.Bytes);
        Assert.Equal(1, inner.AdmittedCalls);
        Assert.Equal(0, inner.OrdinaryCalls);
        Assert.Same(context, inner.LastContext);
        Assert.Same(admission, inner.LastAdmission);
        Assert.Equal(1, admission.Reservations);
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task LifecycleFixtureDoesNotCallOrReserveTheInnerResolver()
    {
        var inner = new RecordingAdmittedResolver();
        var lifecycle = new MarkdownLifecycleImageResolver(inner);
        var admission = new CountingAdmission();

        MarkdownImageResolution result = await lifecycle.ResolveWithSourceByteAdmissionAsync(
            "docs/images/lifecycle-relative.png",
            new MarkdownImageResolveContext(null),
            admission,
            CancellationToken.None);

        Assert.True(result.IsHandled);
        Assert.NotEmpty(result.Asset!.Bytes);
        Assert.Equal(0, inner.AdmittedCalls);
        Assert.Equal(0, inner.OrdinaryCalls);
        Assert.Equal(0, admission.Reservations);
    }

    [Fact]
    public async Task WrapperRetainsOrdinaryResolutionForNonAdmittedResolver()
    {
        var inner = new OrdinaryResolver();
        var lifecycle = new MarkdownLifecycleImageResolver(inner);
        var admission = new CountingAdmission();

        MarkdownImageResolution result = await lifecycle.ResolveWithSourceByteAdmissionAsync(
            "image.png", new MarkdownImageResolveContext(null),
            admission, CancellationToken.None);

        Assert.True(result.IsHandled);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(0, admission.Reservations);
    }

    [Fact]
    public async Task FactoryChainPreservesLeasedCapabilityAndTheExactOwnershipHandle()
    {
        var inner = new LeasedResolver();
        IMarkdownImageResolver lifecycle = MarkdownLifecycleImageResolver.Create(inner);
        var admission = new CountingAdmission();
        var context = new MarkdownImageResolveContext(null);
        var leased = Assert.IsAssignableFrom<IMarkdownLeasedImageResolver>(lifecycle);
        using MarkdownImageResolutionLease result = await leased.ResolveWithSourceLeaseAsync(
            "image.png", context, admission, CancellationToken.None);

        Assert.Same(inner.LastLease, result);
        Assert.Same(admission, inner.LastAdmission);
        Assert.Same(context, inner.LastContext);
        Assert.Equal(3, admission.ActiveBytes);
        Assert.Equal(0, inner.LegacyCalls);
        result.Dispose();
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task LeasedLifecycleFixtureReservesBeforeExposingItsBytesUntilFinalHandle()
    {
        var inner = new LeasedResolver();
        var leased = Assert.IsAssignableFrom<IMarkdownLeasedImageResolver>(
            MarkdownLifecycleImageResolver.Create(inner));
        var admission = new CountingAdmission();
        using MarkdownImageResolutionLease result = await leased.ResolveWithSourceLeaseAsync(
            "docs/images/lifecycle-relative.png", new(null), admission, CancellationToken.None);
        using MarkdownImageResolutionLease retained = result.Retain();

        int length = result.Resolution.Asset!.Bytes.Length;
        Assert.True(length > 0);
        Assert.Equal(length, admission.ActiveBytes);
        Assert.Equal(1, admission.Reservations);
        Assert.Null(inner.LastLease);
        result.Dispose();
        Assert.Equal(length, admission.ActiveBytes);
        retained.Dispose();
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task LeasedLifecycleBlockedFixtureDoesNotReserveOrCallTheInnerResolver()
    {
        var inner = new LeasedResolver();
        var leased = Assert.IsAssignableFrom<IMarkdownLeasedImageResolver>(
            MarkdownLifecycleImageResolver.Create(inner));
        var admission = new CountingAdmission();
        using MarkdownImageResolutionLease result = await leased.ResolveWithSourceLeaseAsync(
            "https://example.invalid/jithub-markdown-lifecycle.png", new(null), admission, CancellationToken.None);

        Assert.Equal(MarkdownImageUnavailableReason.RemoteContentBlocked, result.Resolution.UnavailableReason);
        Assert.Null(inner.LastLease);
        Assert.Equal(0, admission.Reservations);
    }

    [Fact]
    public async Task LeasedLifecycleCancellationAfterGrantReleasesFixtureReservation()
    {
        var inner = new LeasedResolver();
        var leased = Assert.IsAssignableFrom<IMarkdownLeasedImageResolver>(
            MarkdownLifecycleImageResolver.Create(inner));
        using var cancellation = new CancellationTokenSource();
        var admission = new CountingAdmission { AfterGrant = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await leased.ResolveWithSourceLeaseAsync("docs/images/lifecycle-relative.png", new(null),
                admission, cancellation.Token));

        Assert.Equal(1, admission.Reservations);
        Assert.Equal(0, admission.ActiveBytes);
        Assert.Null(inner.LastLease);
    }

    [Fact]
    public void FactoriesDoNotAdvertiseLeasedCapabilityForLegacyResolvers()
    {
        IMarkdownImageResolver lifecycle = MarkdownLifecycleImageResolver.Create(new OrdinaryResolver());
        Assert.False(lifecycle is IMarkdownLeasedImageResolver);
    }

    private sealed class LeasedResolver : IMarkdownLeasedImageResolver
    {
        internal int LegacyCalls { get; private set; }
        internal MarkdownImageResolutionLease? LastLease { get; private set; }
        internal IMarkdownImageSourceByteAdmission? LastAdmission { get; private set; }
        internal MarkdownImageResolveContext? LastContext { get; private set; }

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source, MarkdownImageResolveContext context, CancellationToken cancellationToken)
        {
            LegacyCalls++;
            return ValueTask.FromResult(MarkdownImageResolution.Unavailable);
        }

        public async ValueTask<MarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
            string source, MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission, CancellationToken cancellationToken)
        {
            LastAdmission = admission;
            LastContext = context;
            IDisposable reservation = await admission.ReserveAsync(3, cancellationToken);
            try
            {
                LastLease = new MarkdownImageResolutionLease(
                    MarkdownImageResolution.Resolved(new MarkdownImageAsset([1, 2, 3], "image/png")), reservation);
                return LastLease;
            }
            catch
            {
                reservation.Dispose();
                throw;
            }
        }
    }

    private sealed class RecordingAdmittedResolver : IMarkdownImageSourceByteAdmittedResolver
    {
        public int AdmittedCalls { get; private set; }
        public int OrdinaryCalls { get; private set; }
        public MarkdownImageResolveContext? LastContext { get; private set; }
        public IMarkdownImageSourceByteAdmission? LastAdmission { get; private set; }

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source, MarkdownImageResolveContext context, CancellationToken cancellationToken)
        {
            OrdinaryCalls++;
            return ValueTask.FromResult(MarkdownImageResolution.Unavailable);
        }

        public async ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            AdmittedCalls++;
            LastContext = context;
            LastAdmission = admission;
            using IDisposable lease = await admission.ReserveAsync(3, cancellationToken);
            return MarkdownImageResolution.Resolved(new MarkdownImageAsset([1, 2, 3], "image/png"));
        }
    }

    private sealed class OrdinaryResolver : IMarkdownImageResolver
    {
        public int Calls { get; private set; }

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source, MarkdownImageResolveContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset([1], "image/png")));
        }
    }

    private sealed class CountingAdmission : IMarkdownImageSourceByteAdmission
    {
        private int _activeBytes;

        public bool IsSpeculative => false;
        public long MaximumReservationBytes => 64;
        public int Reservations { get; private set; }
        public int ActiveBytes => Volatile.Read(ref _activeBytes);
        internal Action? AfterGrant { get; init; }

        public ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reservations++;
            Interlocked.Add(ref _activeBytes, checked((int)bytes));
            AfterGrant?.Invoke();
            return ValueTask.FromResult<IDisposable>(new ByteLease(this, checked((int)bytes)));
        }

        private sealed class ByteLease(CountingAdmission owner, int bytes) : IDisposable
        {
            private CountingAdmission? _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(bytes);
        }

        private void Release(int bytes) => Interlocked.Add(ref _activeBytes, -bytes);
    }
}
