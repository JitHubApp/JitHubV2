using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Services;

/// <summary>
/// Optional host capability for resolving GitHub image bytes with ownership that
/// extends through the consumer's use of the returned source buffer.
/// </summary>
/// <remarks>
/// Implementations must reserve source bytes before allocating or materializing
/// them and transfer the reservation into the returned lease. Cache retention is
/// a separate bounded owner and is not covered by this reservation. This capability
/// is intentionally separate from <see cref="IGitHubImageService"/> so legacy
/// providers do not appear to support source-lifetime leases.
/// </remarks>
internal interface IGitHubLeasedImageService
{
    Task<GitHubImageSourceLease?> GetLeasedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default);

    Task<GitHubImageSourceLease?> TryGetCachedLeasedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default);

    Task<GitHubImageSourceLease?> GetOrFetchLeasedAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A retained handle to image bytes produced by <see cref="IGitHubLeasedImageService"/>.
/// </summary>
/// <remarks>
/// <see cref="Image"/> and its byte array are borrowed and immutable while any retained
/// handle is alive. Do not read the bytes after disposing this handle unless another
/// retained handle remains alive. The cache has its own bounded retention budget; this
/// lease covers only the source buffer's active lifetime. Providers must reserve before
/// allocation or stream materialization; this type cannot charge hidden or arbitrary
/// allocations made by custom fetchers. It does not copy source bytes and has no finalizer.
/// Every returned handle must be disposed deterministically.
/// </remarks>
internal sealed partial class GitHubImageSourceLease : IDisposable
{
    private readonly object _handleGate = new();
    private SharedOwner? _owner;

    /// <summary>
    /// Takes ownership of the source reservation only if validation and construction succeed.
    /// </summary>
    internal GitHubImageSourceLease(GitHubCachedImage image, IDisposable sourceReservation)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(sourceReservation);
        if (image.Bytes is not { Length: > 0 })
        {
            throw new ArgumentException(
                "A leased GitHub image must contain nonempty source bytes.",
                nameof(image));
        }

        if (image.RefreshTask is not null)
        {
            throw new ArgumentException(
                "A leased GitHub image cannot expose a byte-bearing refresh task.",
                nameof(image));
        }

        _owner = new SharedOwner(image, sourceReservation);
    }

    private GitHubImageSourceLease(SharedOwner owner) => _owner = owner;

    /// <summary>Gets image metadata and borrowed bytes while this handle is live.</summary>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    internal GitHubCachedImage Image
    {
        get
        {
            lock (_handleGate)
            {
                SharedOwner owner = _owner ?? throw new ObjectDisposedException(
                    nameof(GitHubImageSourceLease));
                return owner.GetImage();
            }
        }
    }

    /// <summary>Creates an independently disposable handle to the same borrowed bytes.</summary>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    internal GitHubImageSourceLease Retain()
    {
        lock (_handleGate)
        {
            SharedOwner owner = _owner ?? throw new ObjectDisposedException(
                nameof(GitHubImageSourceLease));
            owner.AddReference();
            try
            {
                return new GitHubImageSourceLease(owner);
            }
            catch
            {
                owner.ReleaseReference();
                throw;
            }
        }
    }

    public void Dispose()
    {
        SharedOwner? owner;
        lock (_handleGate)
        {
            owner = _owner;
            if (owner is null)
                return;
            _owner = null;
        }

        owner.ReleaseReference();
    }

    private sealed class SharedOwner(GitHubCachedImage image, IDisposable reservation)
    {
        private readonly object _gate = new();
        private GitHubCachedImage? _image = image;
        private IDisposable? _reservation = reservation;
        private int _references = 1;

        internal GitHubCachedImage GetImage()
        {
            lock (_gate)
            {
                return _image ?? throw new ObjectDisposedException(
                    nameof(GitHubImageSourceLease));
            }
        }

        internal void AddReference()
        {
            lock (_gate)
            {
                if (_references == 0 || _image is null)
                {
                    throw new ObjectDisposedException(nameof(GitHubImageSourceLease));
                }

                checked { _references++; }
            }
        }

        internal void ReleaseReference()
        {
            IDisposable? reservation = null;
            lock (_gate)
            {
                if (_references <= 0)
                {
                    throw new InvalidOperationException("The GitHub image reservation was released twice.");
                }

                _references--;
                if (_references == 0)
                {
                    // A disposed handle (including a completed-flight root) must not pin the
                    // source array after its reservation is returned.
                    _image = null;
                    reservation = _reservation;
                    _reservation = null;
                }
            }

            // Remove ownership under the lock before invoking arbitrary admission code.
            reservation?.Dispose();
        }
    }
}
