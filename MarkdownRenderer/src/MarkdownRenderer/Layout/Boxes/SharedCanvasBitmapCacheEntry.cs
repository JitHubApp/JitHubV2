using System;
using System.Threading;
using MarkdownRenderer.Diagnostics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// One uploaded bitmap and its reference-counted lifetime in a
/// <see cref="SharedCanvasBitmapCache"/>: the cache holds one reference and
/// every <see cref="SharedCanvasBitmapCache.Lease"/> holds another, so the
/// bitmap is disposed only after its last owner releases it.
/// </summary>
internal sealed class SharedCanvasBitmapCacheEntry
{
    private readonly SharedCanvasBitmapCache _owner;
    private int _referenceCount = 1;
    private readonly object _bitmapOutputAdmissionGate = new();
    private BitmapOutputAdmission.Reservation? _bitmapOutputReservation;
    private bool _bitmapOutputReservationTransferred;

    // Outside the LRU does not mean outside the unique-owner ledger.
    internal SharedCanvasBitmapCacheEntry(
        SharedCanvasBitmapCache owner,
        CanvasBitmap bitmap,
        BitmapOutputAdmission.Reservation? bitmapReservation = null)
    {
        _owner = owner;
        long pixelWeightBytes = SharedCanvasBitmapCache.InvalidBitmapFallbackWeightBytes;
        int pixelWidth = 0;
        int pixelHeight = 0;
        try
        {
            var pixels = bitmap.SizeInPixels;
            if (pixels.Width > 0 && pixels.Height > 0 &&
                pixels.Width <= int.MaxValue && pixels.Height <= int.MaxValue)
            {
                pixelWidth = (int)pixels.Width;
                pixelHeight = (int)pixels.Height;
                pixelWeightBytes = SharedCanvasBitmapCache.EstimateWeightBytes(pixels.Width, pixels.Height);
            }
            else if (bitmapReservation is not null)
            {
                throw new InvalidOperationException("Bitmap dimensions cannot be represented by the admitted output metadata.");
            }
        }
        catch when (bitmapReservation is null)
        {
            // Device loss must not change legacy ownership or retirement.
        }
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        WeightBytes = pixelWeightBytes;
        PixelWeightBytes = pixelWeightBytes;
        ConfigureBitmapOutputReservation(bitmapReservation);
        _owner.PixelWeightLedger.Register(PixelWeightBytes);
        Bitmap = bitmap;
    }

    internal SharedCanvasBitmapCacheEntry(
        SharedCanvasBitmapCache owner,
        CanvasBitmap bitmap,
        int pixelWidth,
        int pixelHeight,
        Size? intrinsicSize = null,
        BitmapOutputAdmission.Reservation? bitmapReservation = null)
    {
        _owner = owner;
        IntrinsicSize = intrinsicSize;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        PixelWeightBytes = SharedCanvasBitmapCache.EstimateWeightBytes(pixelWidth, pixelHeight);
        WeightBytes = PixelWeightBytes;
        ConfigureBitmapOutputReservation(bitmapReservation);
        _owner.PixelWeightLedger.Register(PixelWeightBytes);
        Bitmap = bitmap;
    }

    internal SharedCanvasBitmapCacheEntry(
        SharedCanvasBitmapCache owner,
        CanvasBitmap bitmap,
        Size? intrinsicSize,
        int? knownPixelWidth,
        int? knownPixelHeight
        , long svgWorkId,
        long svgSourceBytes,
        long svgTargetPixels
        , BitmapOutputAdmission.Reservation? bitmapReservation
    )
    {
        _owner = owner;
        IntrinsicSize = intrinsicSize;
        if (knownPixelWidth.HasValue && knownPixelHeight.HasValue)
        {
            PixelWidth = knownPixelWidth.Value;
            PixelHeight = knownPixelHeight.Value;
            WeightBytes = SharedCanvasBitmapCache.EstimateWeightBytes(knownPixelWidth.Value, knownPixelHeight.Value);
            PixelWeightBytes = WeightBytes;
            ConfigureBitmapOutputReservation(bitmapReservation);
            _owner.PixelWeightLedger.Register(PixelWeightBytes);
            Bitmap = bitmap;
            return;
        }
        using (MarkdownSvgStageTrace.Measure(
            svgWorkId,
            MarkdownSvgWorkStage.SvgCacheWeightSizeQuery,
            svgSourceBytes,
            svgTargetPixels))
        {
        try
        {
            var pixels = bitmap.SizeInPixels;
            if (pixels.Width == 0 || pixels.Height == 0 ||
                pixels.Width > int.MaxValue || pixels.Height > int.MaxValue)
                throw new InvalidOperationException("Bitmap dimensions cannot be represented by the cache metadata.");
            PixelWidth = (int)pixels.Width;
            PixelHeight = (int)pixels.Height;
            WeightBytes = SharedCanvasBitmapCache.EstimateWeightBytes(pixels.Width, pixels.Height);
        }
        catch when (bitmapReservation is null)
        {
            // A device-lost entry may still need normal lease retirement,
            // but it must never be chosen as a preview candidate or evade
            // the bounded cache budget.
            WeightBytes = SharedCanvasBitmapCache.InvalidBitmapFallbackWeightBytes;
        }
        }
        // Finish tracing before transferring ownership/accounting, so a
        // tracing failure cannot leave a phantom retained bitmap charge.
        PixelWeightBytes = WeightBytes;
        ConfigureBitmapOutputReservation(bitmapReservation);
        _owner.PixelWeightLedger.Register(PixelWeightBytes);
        Bitmap = bitmap;
    }

    internal readonly CanvasBitmap Bitmap;

    internal readonly long WeightBytes;

    internal readonly long PixelWeightBytes;

    internal readonly Size? IntrinsicSize;

    internal readonly int PixelWidth;

    internal readonly int PixelHeight;

    internal bool IsBitmapOutputAdmitted
    {
        get
        {
            lock (_bitmapOutputAdmissionGate)
                return _bitmapOutputReservationTransferred &&
                    _bitmapOutputReservation is { IsCommitted: true };
        }
    }

    internal long BitmapOutputAdmissionBytes
    {
        get
        {
            lock (_bitmapOutputAdmissionGate)
                return _bitmapOutputReservationTransferred
                    ? _bitmapOutputReservation?.Bytes ?? 0
                    : 0;
        }
    }

    internal void AddCacheReference() => Interlocked.Increment(ref _referenceCount);

    /// <summary>Owners still holding the bitmap: the cache's reference plus each live lease.</summary>
    internal int ReferenceCount => Volatile.Read(ref _referenceCount);

    private void ConfigureBitmapOutputReservation(
        BitmapOutputAdmission.Reservation? bitmapReservation)
    {
        if (bitmapReservation is null)
            return;

        long actualBytes = PixelWeightBytes;
        long reservedBytes = bitmapReservation.Bytes;
        if (actualBytes <= 0 || reservedBytes < actualBytes)
            throw new InvalidOperationException(
                "The decoded bitmap exceeds its admitted output reservation.");
        if (!bitmapReservation.IsCommitted || bitmapReservation.IsTransferred)
            throw new InvalidOperationException(
                "A bitmap output reservation must be committed and not already transferred before cache ownership.");

        // Do not shrink here: this token may be offered concurrently to
        // another factory, and only the winning Entry is allowed to alter
        // the charge. Shrink only after its one-way transfer succeeds.
        _bitmapOutputReservation = bitmapReservation;
    }

    internal bool TransferBitmapOutputReservationToEntry()
    {
        lock (_bitmapOutputAdmissionGate)
        {
            if (_bitmapOutputReservation is null)
                return true;
            if (_bitmapOutputReservationTransferred)
                return true;
            if (!_bitmapOutputReservation.TransferToEntry())
                return false;
            _bitmapOutputReservationTransferred = true;
            if (!_bitmapOutputReservation.ResizeDown(PixelWeightBytes))
            {
                // Transfer is irreversible. Preserve the conservative
                // original charge rather than publishing an unaccounted
                // bitmap or letting another Entry reuse this token.
                _bitmapOutputReservation.MarkUncertain();
            }
            return true;
        }
    }

    internal bool TryAdmitBitmapOutput(
        SharedCanvasBitmapCache.Lease lease,
        bool isVisible,
        bool isReplacement,
        out long requiredBytes,
        out long capacityVersion)
    {
        requiredBytes = 0;
        capacityVersion = 0;
        lock (_bitmapOutputAdmissionGate)
        {
            // Serialize lazy admission with final release and verify the
            // requesting lease still owns this Entry at the linearization
            // point. A concurrent Dispose then waits to release the bitmap.
            if (!lease.IsAttachedTo(this) || Volatile.Read(ref _referenceCount) <= 0)
                return false;

            if (_bitmapOutputReservationTransferred &&
                _bitmapOutputReservation is { IsCommitted: true })
                return true;

            // A factory's precommitted reservation is not visible to its
            // lease until the factory completes the one-way transfer.
            if (_bitmapOutputReservation is not null || PixelWeightBytes <= 0)
            {
                requiredBytes = PixelWeightBytes;
                return false;
            }

            BitmapOutputAdmission admission = _owner.OutputAdmission;
            if (PixelWeightBytes > admission.MaximumSingleOutputBytes)
            {
                requiredBytes = PixelWeightBytes;
                capacityVersion = admission.GetSnapshot().CapacityVersion;
                return false;
            }

            if (!admission.TryReserve(
                    PixelWeightBytes,
                    isVisible,
                    isReplacement,
                    out BitmapOutputAdmission.Reservation? reservation,
                    out capacityVersion))
            {
                requiredBytes = PixelWeightBytes;
                return false;
            }

            if (reservation is null)
                return false;

            if (TryCommitAndAdoptReservation(reservation))
                return true;
            requiredBytes = PixelWeightBytes;
            return false;
        }
    }

    // Called under the admission gate with a fresh lazy reservation. Commits
    // it and transfers it to this Entry. A reservation that cannot be
    // released while the bitmap is live stays on the Entry as its terminal
    // charge, so another lease cannot admit the same pixels again.
    private bool TryCommitAndAdoptReservation(BitmapOutputAdmission.Reservation reservation)
    {
        try
        {
            if (!reservation.Commit())
            {
                reservation.Dispose();
                return false;
            }
            if (!reservation.TransferToEntry())
            {
                reservation.MarkUncertain();
                // Retain the terminal token on this Entry so another
                // lease cannot charge the same still-live bitmap again.
                _bitmapOutputReservation = reservation;
                return false;
            }

            _bitmapOutputReservation = reservation;
            _bitmapOutputReservationTransferred = true;
            return true;
        }
        catch
        {
            if (reservation.IsCommitted)
            {
                reservation.MarkUncertain();
                // The native bitmap is still alive, so retain this
                // terminal charge on the Entry and prevent a later
                // lease from admitting the same pixels a second time.
                _bitmapOutputReservation = reservation;
            }
            else
                reservation.Dispose();
            throw;
        }
    }

    internal void AbandonUnpublishedOwnership()
    {
        // Only factory failure before cache commit can call this. No native
        // Dispose: the failed factory has not consumed the caller's bitmap.
        lock (_bitmapOutputAdmissionGate)
        {
            if (_bitmapOutputReservationTransferred)
                throw new InvalidOperationException("Transferred bitmap admission cannot be abandoned.");
            _bitmapOutputReservation = null;
            Volatile.Write(ref _referenceCount, 0);
        }
        _owner.PixelWeightLedger.AbandonRegistration(PixelWeightBytes);
    }

    internal bool TryAcquire()
    {
        int observed = Volatile.Read(ref _referenceCount);
        while (observed > 0)
        {
            int updated = Interlocked.CompareExchange(ref _referenceCount, observed + 1, observed);
            if (updated == observed)
                return true;
            observed = updated;
        }

        return false;
    }

    internal void ReleaseCacheReference()
    {
        try
        {
            ReleaseReference();
        }
        catch (Exception exception)
        {
            _owner.RecordCacheDisposalFailure(exception);
        }
    }

    internal void Release() => ReleaseReference();

    private void ReleaseReference()
    {
        BitmapOutputAdmission.Reservation? reservation;
        lock (_bitmapOutputAdmissionGate)
        {
            if (Interlocked.Decrement(ref _referenceCount) != 0)
                return;
            reservation = _bitmapOutputReservationTransferred
                ? _bitmapOutputReservation
                : null;
        }

        try
        {
            Bitmap.Dispose();
        }
        catch
        {
            _owner.PixelWeightLedger.RecordUncertainDispose(PixelWeightBytes);
            reservation?.MarkUncertain();
            throw;
        }

        _owner.PixelWeightLedger.ReleaseSuccessfulDispose(PixelWeightBytes);
        reservation?.ReleaseAfterSuccessfulCleanup();
    }
}
