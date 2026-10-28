using System;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Performance;

/// <summary>
/// A disposable handle that keeps a resolved image's source-byte reservation alive.
/// </summary>
/// <remarks>
/// The asset bytes in <see cref="Resolution"/> are borrowed and must be treated as immutable.
/// Keep this lease alive for every operation that reads those bytes. A copied resolution or
/// asset does not extend the lease lifetime. This type does not copy the source buffer and has
/// no finalizer; callers must dispose every handle deterministically.
/// </remarks>
public sealed class MarkdownImageResolutionLease : IDisposable, IMarkdownImageResolutionLease
{
    private readonly object _handleGate = new();
    private SharedOwner? _owner;

    /// <summary>
    /// Creates a lease and takes ownership of <paramref name="sourceReservation"/> only when
    /// construction succeeds.
    /// </summary>
    /// <param name="resolution">The resolver result whose bytes are borrowed through this lease.</param>
    /// <param name="sourceReservation">
    /// The admission reservation covering the source buffer, or <see langword="null"/> when
    /// the result has no nonempty asset. A supplied reservation is disposed with the final
    /// retained lease handle, including when the result has no asset.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The result contains nonempty bytes but no reservation, or its asset has a null byte array.
    /// </exception>
    /// <remarks>
    /// A resolver must obtain admission before allocating or retaining source bytes and return
    /// the matching reservation here. This API cannot detect or charge arbitrary allocations
    /// hidden inside a resolver. Resolver caches are separate owners and require their own
    /// bounded cache policy; this lease only covers bytes owned through its handles.
    /// </remarks>
    public MarkdownImageResolutionLease(
        MarkdownImageResolution resolution,
        IDisposable? sourceReservation)
        : this(resolution, sourceReservation, allowLegacyUnreservedAsset: false)
    {
    }

    private MarkdownImageResolutionLease(
        MarkdownImageResolution resolution,
        IDisposable? sourceReservation,
        bool allowLegacyUnreservedAsset)
    {
        MarkdownImageAsset? asset = resolution.Asset;
        if (asset is not null)
        {
            byte[] bytes = asset.Bytes ?? throw new ArgumentException(
                "A resolved image asset must have a byte array.",
                nameof(resolution));
            if (!allowLegacyUnreservedAsset && bytes.Length != 0 && sourceReservation is null)
            {
                throw new ArgumentException(
                    "A nonempty image asset requires a source-byte reservation.",
                    nameof(sourceReservation));
            }
        }

        // Do not store or dispose the reservation until validation has completed. If
        // construction fails, ownership remains with the caller.
        _owner = new SharedOwner(resolution, sourceReservation);
    }

    // Compatibility adapter for legacy resolver results that predate source leases. New
    // leased providers must use the validating public constructor and transfer admission.
    internal static MarkdownImageResolutionLease CreateUnreserved(
        MarkdownImageResolution resolution) =>
        new(resolution, sourceReservation: null, allowLegacyUnreservedAsset: true);

    private MarkdownImageResolutionLease(SharedOwner owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Gets the borrowed resolution while this handle is live.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    /// <remarks>
    /// The returned value does not itself retain the source-byte reservation. Do not use its
    /// asset bytes after disposing this handle unless another retained handle remains alive.
    /// </remarks>
    public MarkdownImageResolution Resolution
    {
        get
        {
            lock (_handleGate)
            {
                SharedOwner owner = _owner ?? throw new ObjectDisposedException(
                    nameof(MarkdownImageResolutionLease));
                return owner.GetResolution();
            }
        }
    }

    MarkdownImageResolution IMarkdownImageResolutionLease.Resolution => Resolution;

    /// <summary>
    /// Creates an independently disposable handle to the same borrowed resolution.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public MarkdownImageResolutionLease Retain()
    {
        lock (_handleGate)
        {
            SharedOwner owner = _owner ?? throw new ObjectDisposedException(
                nameof(MarkdownImageResolutionLease));
            owner.AddReference();
            try
            {
                return new MarkdownImageResolutionLease(owner);
            }
            catch
            {
                // The current handle still owns a reference, so this rollback cannot
                // release the reservation or invoke external code.
                owner.ReleaseReference();
                throw;
            }
        }
    }

    IMarkdownImageResolutionLease IMarkdownImageResolutionLease.Retain() => Retain();

    /// <summary>
    /// Releases this handle. The source reservation is released once, after the final retained
    /// handle is disposed. Repeated calls are harmless even if reservation disposal throws.
    /// </summary>
    public void Dispose()
    {
        SharedOwner? owner;
        lock (_handleGate)
        {
            owner = _owner;
            if (owner is null)
            {
                return;
            }

            _owner = null;
        }

        owner.ReleaseReference();
    }

    private sealed class SharedOwner(
        MarkdownImageResolution resolution,
        IDisposable? reservation)
    {
        private readonly object _gate = new();
        private MarkdownImageResolution? _resolution = resolution;
        private IDisposable? _reservation = reservation;
        private int _references = 1;

        internal MarkdownImageResolution GetResolution()
        {
            lock (_gate)
            {
                return _resolution ?? throw new ObjectDisposedException(
                    nameof(MarkdownImageResolutionLease));
            }
        }

        internal void AddReference()
        {
            lock (_gate)
            {
                if (_references == 0 || _resolution is null)
                {
                    throw new ObjectDisposedException(nameof(MarkdownImageResolutionLease));
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
                    throw new InvalidOperationException("The source reservation was released twice.");
                }

                _references--;
                if (_references == 0)
                {
                    // Drop the only shared reference to the result before returning quota.
                    // Disposed wrappers retain this SharedOwner, but it no longer retains
                    // the asset or its byte array.
                    _resolution = null;
                    reservation = _reservation;
                    _reservation = null;
                }
            }

            // External admission code must never run while the owner lock is held. The
            // reservation is removed first, so even an exception cannot cause a second call.
            reservation?.Dispose();
        }
    }
}
