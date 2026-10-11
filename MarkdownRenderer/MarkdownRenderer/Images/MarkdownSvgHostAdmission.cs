using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Images;

/// <summary>
/// Optional engine-neutral host admission. A capacity denial is transient and
/// must never become a content failure. Deferred hosts retain demand, not bytes.
/// </summary>
internal interface IMarkdownSvgHostAdmission
{
    object AdmissionDomain { get; }
    long CapacityVersion { get; }

    // Implementations check free capacity for the required peak at the current
    // demand tier and subscribe atomically. A numeric version pulse alone is
    // not readiness. Signals cannot invoke host code from a retirement lock.
    // This source-free wait owns no input bytes and grants no reservation;
    // admission is still rechecked by the subsequent open attempt.
    Task WaitForCapacityAsync(
        long requiredFreeBytes,
        MarkdownSvgRenderPriority priority,
        CancellationToken cancellationToken);

    ValueTask<MarkdownSvgHostOpenResult> OpenForHostAsync(
        MarkdownSvgOpenRequest request,
        MarkdownSvgDemandTicket demand,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional progressive-host admission for the provider's synchronous source
/// preparation byte buffers. This uses the same domain as provider opening;
/// caller buffers, caches, XML objects and native resources are not this lease.
/// </summary>
internal interface IMarkdownSvgHostPreparationAdmission : IMarkdownSvgHostAdmission
{
    // Nonblocking. Report the full conservative preparation peak on success
    // and denial, never a free-capacity delta or an input-length estimate.
    // A successful lease owns no source and can only shrink or retire.
    bool TryReserveHostPreparationScratch(
        MarkdownSvgDemandTicket demand,
        out IMarkdownSvgHostPreparationLease? lease,
        out long observedCapacityVersion,
        out long requiredFreeBytes);
}

internal interface IMarkdownSvgHostPreparationLease : IDisposable
{
    void SetLength(long retainedBytes);
}

/// <summary>Optional queued-render promotion without changing the public DTOs.</summary>
internal interface IMarkdownSvgHostDocument
{
    ValueTask<MarkdownSvgRaster> RenderForHostAsync(
        MarkdownSvgRenderRequest request,
        MarkdownSvgDemandTicket demand,
        CancellationToken cancellationToken);
}

internal enum MarkdownSvgHostOpenStatus
{
    // A default result is malformed, not a zero-weight capacity wait.
    Invalid,
    CapacityUnavailable,
    Opened,
}

internal readonly struct MarkdownSvgHostOpenResult
{
    private MarkdownSvgHostOpenResult(
        MarkdownSvgHostOpenStatus status,
        IMarkdownSvgDocument? document,
        long sourceReservationBytes,
        object? admissionDomain,
        long capacityVersion,
        long requiredSourceReservationBytes)
    {
        Status = status;
        Document = document;
        SourceReservationBytes = sourceReservationBytes;
        AdmissionDomain = admissionDomain;
        CapacityVersion = capacityVersion;
        RequiredSourceReservationBytes = requiredSourceReservationBytes;
    }

    internal MarkdownSvgHostOpenStatus Status { get; }
    internal IMarkdownSvgDocument? Document { get; }
    internal long SourceReservationBytes { get; }
    internal object? AdmissionDomain { get; }
    internal long CapacityVersion { get; }
    internal long RequiredSourceReservationBytes { get; }

    // The consumer owns this document even if its generation became stale
    // after opening. Validate the domain by reference identity before commit;
    // stale/canceled/rejected candidates must still retire the handle.
    internal static MarkdownSvgHostOpenResult Opened(
        IMarkdownSvgDocument document,
        long sourceReservationBytes,
        object admissionDomain)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(admissionDomain);
        if (sourceReservationBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceReservationBytes));
        return new(MarkdownSvgHostOpenStatus.Opened, document, sourceReservationBytes,
            admissionDomain, 0, 0);
    }

    internal static MarkdownSvgHostOpenResult CapacityUnavailable(
        long capacityVersion,
        long requiredSourceReservationBytes)
    {
        if (capacityVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(capacityVersion));
        if (requiredSourceReservationBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredSourceReservationBytes));
        return new(MarkdownSvgHostOpenStatus.CapacityUnavailable, null, 0,
            null, capacityVersion, requiredSourceReservationBytes);
    }
}
