using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images;

internal sealed class LeasedSvgResolver(Func<byte[]> createBytes, int byteLength) : IMarkdownLeasedImageResolver
{
    private int _bufferAllocationCount;

    internal int BufferAllocationCount => Volatile.Read(ref _bufferAllocationCount);

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(MarkdownImageResolution.NotHandled);

    public async ValueTask<MarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        IDisposable reservation = await admission.ReserveAsync(byteLength, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            byte[] bytes = createBytes();
            if (bytes.Length != byteLength)
                throw new InvalidOperationException("The test resolver returned a buffer of an unexpected size.");
            Interlocked.Increment(ref _bufferAllocationCount);
            var result = MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                bytes,
                "image/svg+xml",
                ResolvedUri: new Uri(source),
                CacheKey: source));
            return new MarkdownImageResolutionLease(result, reservation);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }
}
