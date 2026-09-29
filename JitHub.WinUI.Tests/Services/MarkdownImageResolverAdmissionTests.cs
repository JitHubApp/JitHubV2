using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MarkdownImageResolverAdmissionTests
{
    [Fact]
    public async Task AuditAndLifecycleWrappersPreserveAdmittedResolution()
    {
        var inner = new RecordingAdmittedResolver();
        var lifecycle = new MarkdownLifecycleImageResolver(inner);
        var audit = new MarkdownAuditImageResolver(lifecycle);
        var admission = new CountingAdmission();
        var context = new MarkdownImageResolveContext(new Uri("https://github.com/example/repo/"));

        MarkdownImageResolution result = await audit.ResolveWithSourceByteAdmissionAsync(
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
        var audit = new MarkdownAuditImageResolver(inner);
        var admission = new CountingAdmission();

        MarkdownImageResolution result = await audit.ResolveWithSourceByteAdmissionAsync(
            "image.png", new MarkdownImageResolveContext(null),
            admission, CancellationToken.None);

        Assert.True(result.IsHandled);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(0, admission.Reservations);
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

        public ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reservations++;
            Interlocked.Add(ref _activeBytes, checked((int)bytes));
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
