using System;

namespace MarkdownRenderer.Images;

/// <summary>
/// The result of <see cref="IMarkdownSvgAdmission.TryOpenAsync"/>: an opened
/// document, or the capacity the open needs.
/// </summary>
public readonly struct MarkdownSvgAdmissionResult
{
    private MarkdownSvgAdmissionResult(
        MarkdownSvgAdmissionStatus status,
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

    /// <summary>Gets whether the document opened.</summary>
    public MarkdownSvgAdmissionStatus Status { get; }

    /// <summary>
    /// Gets the opened document. The caller owns it, even if it no longer
    /// needs it, and must dispose it.
    /// </summary>
    public IMarkdownSvgDocument? Document { get; }

    /// <summary>Gets the bytes the opened document holds against capacity.</summary>
    public long SourceReservationBytes { get; }

    /// <summary>
    /// Gets the capacity the document was opened from. Compare it by reference
    /// with <see cref="IMarkdownSvgAdmission.AdmissionDomain"/> before using
    /// the document.
    /// </summary>
    public object? AdmissionDomain { get; }

    /// <summary>Gets the capacity version a denial observed.</summary>
    public long CapacityVersion { get; }

    /// <summary>Gets the bytes a denied open needs to be free.</summary>
    public long RequiredSourceReservationBytes { get; }

    /// <summary>Creates a result for an opened document.</summary>
    /// <param name="document">The opened document, which the caller will own.</param>
    /// <param name="sourceReservationBytes">The bytes it holds against capacity.</param>
    /// <param name="admissionDomain">The capacity it was opened from.</param>
    public static MarkdownSvgAdmissionResult Opened(
        IMarkdownSvgDocument document,
        long sourceReservationBytes,
        object admissionDomain)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(admissionDomain);
        if (sourceReservationBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceReservationBytes));
        return new(MarkdownSvgAdmissionStatus.Opened, document, sourceReservationBytes,
            admissionDomain, 0, 0);
    }

    /// <summary>Creates a result for an open that needs more free capacity.</summary>
    /// <param name="capacityVersion">The capacity version the decision observed.</param>
    /// <param name="requiredSourceReservationBytes">The bytes the open needs to be free.</param>
    public static MarkdownSvgAdmissionResult CapacityUnavailable(
        long capacityVersion,
        long requiredSourceReservationBytes)
    {
        if (capacityVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(capacityVersion));
        if (requiredSourceReservationBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredSourceReservationBytes));
        return new(MarkdownSvgAdmissionStatus.CapacityUnavailable, null, 0,
            null, capacityVersion, requiredSourceReservationBytes);
    }
}
