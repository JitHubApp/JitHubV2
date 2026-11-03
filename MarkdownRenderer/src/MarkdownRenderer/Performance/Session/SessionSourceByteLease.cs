using System;
using System.Threading;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Keeps source-byte admission and session shutdown accounting alive for the
/// full lifetime of a transferable image result, not just until the resolver
/// returns. Cleanup attempts every owned release exactly once.
/// </summary>
internal sealed class SessionSourceByteLease(
    MarkdownPerformanceSessionLifetime lifetime,
    IDisposable admissionLease,
    CancellationTokenSource linkedCancellation) : IDisposable
{
    private MarkdownPerformanceSessionLifetime? _lifetime = lifetime;
    private IDisposable? _admissionLease = admissionLease;
    private CancellationTokenSource? _linkedCancellation = linkedCancellation;

    public void Dispose()
    {
        MarkdownPerformanceSessionLifetime? owner = Interlocked.Exchange(ref _lifetime, null);
        if (owner is null)
            return;

        IDisposable? admission = Interlocked.Exchange(ref _admissionLease, null);
        CancellationTokenSource? linked = Interlocked.Exchange(ref _linkedCancellation, null);
        try
        {
            try
            {
                admission?.Dispose();
            }
            catch (Exception exception)
            {
                MarkdownSessionAdmissions.LogLeaseCleanupFailure("source-byte admission", exception);
            }

            try
            {
                linked?.Dispose();
            }
            catch (Exception exception)
            {
                MarkdownSessionAdmissions.LogLeaseCleanupFailure("source-byte cancellation", exception);
            }
        }
        finally
        {
            owner.RetireWork();
        }
    }
}
