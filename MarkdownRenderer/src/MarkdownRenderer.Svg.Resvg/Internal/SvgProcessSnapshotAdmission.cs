using System.Runtime.InteropServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Bounds provider-owned input-clone and normalized-source byte-array capacity
/// across all renderer instances in this process, including the admitted peak
/// where the clone, segmented output and compacted tail overlap. It also backs
/// opt-in host-preparation output byte buffers when the host explicitly obtains
/// a preparation lease before calling <c>PrepareSource</c>; unleased/default
/// preparation paths are not included. This is not a whole-preflight or process
/// memory bound: caller and source-cache buffers, XML/parser objects and strings,
/// other preflight temporaries, worker mappings, decoded images, parsed
/// resources, GPU allocations, and RSS remain outside this reservation.
/// </summary>
internal sealed class SvgProcessSnapshotAdmission
{
    private static readonly object ProcessDomainToken = new();

    private readonly object _gate = new();
    private readonly long _capacityBytes;
    private readonly long _visibleHeadroomBytes;
    private readonly object _domain;
    // Allocate a waiter only when a host actually subscribes. Retiring a
    // snapshot must stay allocation-free so memory pressure cannot strand its
    // lease or make a completed document lose its only release ticket.
    private TaskCompletionSource? _capacityChanged;
    private long _reservedBytes;
    private long _capacityVersion;

    internal static SvgProcessSnapshotAdmission Process { get; } = CreateProcessOwner();

    internal SvgProcessSnapshotAdmission(long capacityBytes, long visibleHeadroomBytes)
        : this(capacityBytes, visibleHeadroomBytes, new object())
    {
    }

    private SvgProcessSnapshotAdmission(long capacityBytes, long visibleHeadroomBytes, object domain)
    {
        if (capacityBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        if (visibleHeadroomBytes < 0 || visibleHeadroomBytes >= capacityBytes)
            throw new ArgumentOutOfRangeException(nameof(visibleHeadroomBytes));

        _capacityBytes = capacityBytes;
        _visibleHeadroomBytes = visibleHeadroomBytes;
        _domain = domain;
    }

    internal object AdmissionDomain => _domain;
    internal long CapacityBytes => _capacityBytes;
    internal long VisibleHeadroomBytes => _visibleHeadroomBytes;

    internal long CapacityVersion
    {
        get { lock (_gate) return _capacityVersion; }
    }

    internal long ReservedBytes
    {
        get { lock (_gate) return _reservedBytes; }
    }

    internal bool TryReserve(
        long bytes,
        MarkdownSvgRenderPriority priority,
        out Lease? lease,
        out long observedCapacityVersion)
    {
        if (bytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (priority is not (MarkdownSvgRenderPriority.Visible or MarkdownSvgRenderPriority.Overscan))
            throw new ArgumentOutOfRangeException(nameof(priority));

        lock (_gate)
        {
            observedCapacityVersion = _capacityVersion;
            long admissionLimit = priority == MarkdownSvgRenderPriority.Visible
                ? _capacityBytes
                : _capacityBytes - _visibleHeadroomBytes;
            if (bytes > admissionLimit - _reservedBytes)
            {
                lease = null;
                return false;
            }

            // Construct the lease before publishing its weight. If allocation
            // fails, the process-wide counter remains unchanged.
            Lease created = new(this, bytes);
            checked { _reservedBytes += bytes; }
            lease = created;
            return true;
        }
    }

    /// <summary>
    /// Waits for a capacity transition newer than <paramref name="observedVersion"/>.
    /// The version check and task capture share the admission lock, so a release
    /// between denial and subscription is never lost.
    /// </summary>
    internal Task WaitForCapacityChangeAsync(
        long observedVersion,
        CancellationToken cancellationToken)
    {
        if (observedVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(observedVersion));

        Task wait;
        lock (_gate)
        {
            if (_capacityVersion != observedVersion)
            {
                wait = Task.CompletedTask;
            }
            else
            {
                // Subscribe and compare under the same lock. The only
                // allocation is on the waiter path, never on lease retirement.
                _capacityChanged ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _capacityChanged.Task;
            }
        }

        return cancellationToken.CanBeCanceled
            ? wait.WaitAsync(cancellationToken)
            : wait;
    }

    /// <summary>
    /// Waits until at least <paramref name="requiredFreeBytes"/> can be
    /// admitted at the requested priority. A capacity version change that does
    /// not satisfy this threshold is not surfaced as readiness.
    /// </summary>
    internal async Task WaitForCapacityAsync(
        long requiredFreeBytes,
        MarkdownSvgRenderPriority priority,
        CancellationToken cancellationToken)
    {
        if (requiredFreeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredFreeBytes));
        if (priority is not (MarkdownSvgRenderPriority.Visible or MarkdownSvgRenderPriority.Overscan))
            throw new ArgumentOutOfRangeException(nameof(priority));
        if (requiredFreeBytes > GetAdmissionLimit(priority))
            throw new ArgumentOutOfRangeException(nameof(requiredFreeBytes),
                "The requested capacity cannot fit in the selected admission tier.");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task wait;
            lock (_gate)
            {
                if (requiredFreeBytes <= GetAdmissionLimit(priority) - _reservedBytes)
                    return;

                // Check the actual threshold and capture the current signal
                // under one gate. A release between denial and subscription
                // is either observed by the check or wakes this task.
                _capacityChanged ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _capacityChanged.Task;
            }

            if (cancellationToken.CanBeCanceled)
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            else
                await wait.ConfigureAwait(false);

            // Any release may have been too small or consumed by a competing
            // reservation. Recheck the threshold before reporting readiness.
        }
    }

    private bool TryGrow(
        long previousBytes,
        long targetBytes,
        MarkdownSvgRenderPriority priority,
        out long observedCapacityVersion)
    {
        lock (_gate)
        {
            observedCapacityVersion = _capacityVersion;
            if (targetBytes < previousBytes || previousBytes <= 0 || _reservedBytes < previousBytes)
                throw new InvalidOperationException("The SVG source reservation cannot grow from its current owner state.");

            long additionalBytes = targetBytes - previousBytes;
            if (additionalBytes == 0)
                return true;

            if (additionalBytes > GetAdmissionLimit(priority) - _reservedBytes)
                return false;

            // The caller holds the lease gate, so its weight cannot be
            // concurrently shrunk/disposed while this process-wide delta is
            // published. Growth consumes capacity and does not advance or
            // signal the release version.
            _reservedBytes = checked(_reservedBytes + additionalBytes);
            return true;
        }
    }

    private long GetAdmissionLimit(MarkdownSvgRenderPriority priority) =>
        priority == MarkdownSvgRenderPriority.Visible
            ? _capacityBytes
            : _capacityBytes - _visibleHeadroomBytes;

    private TaskCompletionSource? Shrink(long previousBytes, long nextBytes)
    {
        lock (_gate)
        {
            if (nextBytes > previousBytes || nextBytes < 0 || _reservedBytes < previousBytes)
                throw new InvalidOperationException("The SVG source reservation cannot grow or underflow.");

            if (nextBytes == previousBytes)
                return null;

            TaskCompletionSource? signal = AdvanceVersionLocked();
            _reservedBytes -= previousBytes - nextBytes;
            return signal;
        }
    }

    private TaskCompletionSource? Release(long bytes)
    {
        lock (_gate)
        {
            if (bytes <= 0 || _reservedBytes < bytes)
                throw new InvalidOperationException("The SVG source reservation accounting underflowed.");

            TaskCompletionSource? signal = AdvanceVersionLocked();
            _reservedBytes -= bytes;
            return signal;
        }
    }

    private TaskCompletionSource? AdvanceVersionLocked()
    {
        long nextVersion = checked(_capacityVersion + 1);
        // Detach the current waiter and advance the version without allocating.
        // A subsequent waiter lazily creates the next signal under _gate.
        TaskCompletionSource? previous = _capacityChanged;
        _capacityVersion = nextVersion;
        _capacityChanged = null;
        return previous;
    }

    private static SvgProcessSnapshotAdmission CreateProcessOwner()
    {
        bool wideProcess = RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;
        long capacityBytes = wideProcess ? 64L * 1024 * 1024 : 32L * 1024 * 1024;
        long visibleHeadroomBytes = wideProcess ? 16L * 1024 * 1024 : 8L * 1024 * 1024;
        return new SvgProcessSnapshotAdmission(capacityBytes, visibleHeadroomBytes, ProcessDomainToken);
    }

    internal sealed class Lease : IDisposable, IMarkdownSvgHostPreparationLease
    {
        private readonly object _gate = new();
        private SvgProcessSnapshotAdmission? _owner;
        private long _bytes;

        internal Lease(SvgProcessSnapshotAdmission owner, long bytes)
        {
            _owner = owner;
            _bytes = bytes;
        }

        internal long Bytes
        {
            get { lock (_gate) return _bytes; }
        }

        /// <summary>
        /// Atomically increases this lease if the current tier has enough
        /// unreserved process capacity. This operation never waits or signals
        /// capacity listeners.
        /// </summary>
        internal bool TryGrow(
            long targetBytes,
            MarkdownSvgRenderPriority priority,
            out long observedCapacityVersion)
        {
            if (targetBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(targetBytes));
            if (priority is not (MarkdownSvgRenderPriority.Visible or MarkdownSvgRenderPriority.Overscan))
                throw new ArgumentOutOfRangeException(nameof(priority));

            lock (_gate)
            {
                SvgProcessSnapshotAdmission owner = _owner ?? throw new ObjectDisposedException(nameof(Lease));
                long previous = _bytes;
                if (targetBytes < previous)
                    throw new ArgumentOutOfRangeException(nameof(targetBytes), "TryGrow cannot shrink a reservation.");
                bool grew = owner.TryGrow(previous, targetBytes, priority, out observedCapacityVersion);
                if (grew)
                    _bytes = targetBytes;
                return grew;
            }
        }

        internal void SetLength(long bytes)
        {
            if (bytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(bytes));

            TaskCompletionSource? signal;
            lock (_gate)
            {
                SvgProcessSnapshotAdmission owner = _owner ?? throw new ObjectDisposedException(nameof(Lease));
                long previous = _bytes;
                if (bytes > previous)
                    throw new InvalidOperationException("A normalized SVG source cannot exceed its preflight reservation.");
                if (bytes == previous)
                    return;
                // Serialize shrink against Dispose. Otherwise Dispose could
                // release the new weight before the owner applies the delta.
                // Signal only after the lease lock is released too.
                signal = owner.Shrink(previous, bytes);
                _bytes = bytes;
            }
            signal?.TrySetResult();
        }

        void IMarkdownSvgHostPreparationLease.SetLength(long retainedBytes) => SetLength(retainedBytes);

        public void Dispose()
        {
            TaskCompletionSource? signal;
            lock (_gate)
            {
                SvgProcessSnapshotAdmission? owner = _owner;
                if (owner is null)
                    return;

                // Keep ownership until the owner has recorded the release. If
                // version publication cannot allocate, a later Dispose can
                // retry rather than orphaning the reservation.
                signal = owner.Release(_bytes);
                _owner = null;
                _bytes = 0;
            }

            // Capacity changes can wake host code; never signal under either
            // the lease or admission-owner lock.
            signal?.TrySetResult();
        }
    }
}
