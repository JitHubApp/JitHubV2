using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Images;

/// <summary>
/// Admission for the bytes held while one image source is being read or decoded
/// by a host resolver. A lease must cover the actual buffer lifetime and be
/// released before recursively resolving another image (for example Git LFS).
/// This admission does not replace the host's hard per-image size limit.
/// </summary>
public interface IMarkdownImageSourceByteAdmission
{
    /// <summary>True when this resolution is speculative rather than visible.</summary>
    bool IsSpeculative { get; }

    /// <summary>
    /// Largest reservation this request may make. Speculative requests have a
    /// smaller ceiling so visible image reads retain capacity.
    /// </summary>
    long MaximumReservationBytes { get; }

    /// <summary>
    /// Waits for a weighted reservation. Dispose the returned lease as soon as
    /// the read or decode buffer is no longer in flight, including on failure.
    /// </summary>
    ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken);
}
