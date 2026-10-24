using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

[Trait("Category", "ReleaseSecurity")]
public sealed class MarkdownLeasedAuditImageResolverTests
{
    [Fact]
    public void FactoryDoesNotAdvertiseLeasesForLegacyResolver()
    {
        IMarkdownImageResolver wrapped = MarkdownAuditImageResolver.Create(new LegacyResolver());

        Assert.IsNotAssignableFrom<IMarkdownLeasedImageResolver>(wrapped);
        Assert.IsAssignableFrom<IMarkdownImageSourceByteAdmittedResolver>(wrapped);
        Assert.IsAssignableFrom<IMarkdownImagePrefetcher>(wrapped);
    }

    [Fact]
    public async Task FactoryPreservesLeaseCapabilityAndReturnsTheSameOwnershipHandle()
    {
        var admission = new RecordingAdmission();
        var inner = new LeasedResolver(admission);
        IMarkdownImageResolver wrapped = MarkdownAuditImageResolver.Create(inner);
        IMarkdownLeasedImageResolver leased = Assert.IsAssignableFrom<IMarkdownLeasedImageResolver>(wrapped);

        MarkdownImageResolutionLease result = await leased.ResolveWithSourceLeaseAsync(
            "https://fixture.example/image.png",
            new MarkdownImageResolveContext(null),
            admission,
            CancellationToken.None);

        Assert.Same(inner.LastLease, result);
        Assert.Same(admission, inner.LastAdmission);
        Assert.Equal(3, result.Resolution.Asset?.Bytes.Length);
        Assert.Equal(0, admission.DisposeCount);
        result.Dispose();
        Assert.Equal(1, admission.DisposeCount);
    }

    [Fact]
    public async Task FactoryForwardsCancellationWithoutCreatingAResolutionLease()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var admission = new RecordingAdmission();
        var inner = new LeasedResolver(admission);
        IMarkdownLeasedImageResolver wrapped = Assert.IsAssignableFrom<IMarkdownLeasedImageResolver>(
            MarkdownAuditImageResolver.Create(inner));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await wrapped.ResolveWithSourceLeaseAsync(
                "https://fixture.example/image.png",
                new MarkdownImageResolveContext(null),
                admission,
                cancellation.Token));

        Assert.Null(inner.LastLease);
        Assert.Equal(0, admission.ReservationCount);
        Assert.Equal(0, admission.DisposeCount);
    }

    private sealed class LegacyResolver : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Offline));
    }

    private sealed class LeasedResolver(RecordingAdmission expectedAdmission) : IMarkdownLeasedImageResolver
    {
        public MarkdownImageResolutionLease? LastLease { get; private set; }
        public IMarkdownImageSourceByteAdmission? LastAdmission { get; private set; }

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Offline));

        public async ValueTask<MarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Same(expectedAdmission, admission);
            LastAdmission = admission;
            IDisposable reservation = await admission.ReserveAsync(3, cancellationToken).ConfigureAwait(false);
            var lease = new MarkdownImageResolutionLease(
                MarkdownImageResolution.Resolved(new MarkdownImageAsset([1, 2, 3], "image/png")),
                reservation);
            LastLease = lease;
            return lease;
        }
    }

    private sealed class RecordingAdmission : IMarkdownImageSourceByteAdmission
    {
        private int _reservationCount;
        private int _disposeCount;

        public bool IsSpeculative => false;
        public long MaximumReservationBytes => 1024;
        public int ReservationCount => Volatile.Read(ref _reservationCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _reservationCount);
            return ValueTask.FromResult<IDisposable>(new Reservation(this));
        }

        private sealed class Reservation(RecordingAdmission owner) : IDisposable
        {
            private RecordingAdmission? _owner = owner;

            public void Dispose()
            {
                RecordingAdmission? owner = Interlocked.Exchange(ref _owner, null);
                if (owner is not null)
                    Interlocked.Increment(ref owner._disposeCount);
            }
        }
    }
}
