namespace JitHub.Services.Markdown;

internal enum FirstViewportImagesReadyPaintPollAction
{
    NotPolled = -1,
    WaitingForPaint = 0,
    RearmForCurrentViewport = 1,
    Ready = 2,
}

/// <summary>
/// Keeps the one-shot Canvas paint acknowledgement tied to its requested render generation.
/// A poll that finds an accepted paint no longer describes the current viewport must discard
/// that acknowledgement and request a new paint; it must never reuse the old timestamp.
/// </summary>
internal sealed class FirstViewportImagesReadyPaintAcknowledgementLifecycle
{
    private long _requestedGeneration;
    private long _currentPaintedGeneration;
    private long _currentPaintedAtTicks;
    private long _lastPaintedGeneration;
    private long _lastPaintedAtTicks;
    private bool? _lastPaintProbeAccepted;
    private bool? _lastPaintAcknowledgementRecorded;
    private bool _paintRearmIssued;

    internal long RequestedGeneration => _requestedGeneration;

    // Preserve the last successful callback for diagnostics even after its
    // acknowledgement is invalidated and the probe is re-armed. The poll
    // action/current-ack fields distinguish this historical paint from a
    // currently valid acknowledgement.
    internal long PaintedGeneration => _lastPaintedGeneration;

    internal long PaintedAtTicks => _lastPaintedAtTicks;

    internal bool? LastPaintProbeAccepted => _lastPaintProbeAccepted;

    internal bool? LastPaintAcknowledgementRecorded => _lastPaintAcknowledgementRecorded;

    internal void Begin(long generation)
    {
        _requestedGeneration = generation > 0 ? generation : 0;
        ClearAcknowledgement();
        _lastPaintedGeneration = 0;
        _lastPaintedAtTicks = 0;
        _paintRearmIssued = false;
        ClearLastCallbackOutcome();
    }

    internal void RecordPaintCallbackOutcome(bool? probeAccepted, bool? acknowledgementRecorded)
    {
        _lastPaintProbeAccepted = probeAccepted;
        _lastPaintAcknowledgementRecorded = acknowledgementRecorded;
    }

    internal bool RecordAcceptedPaint(long generation, long paintedAtTicks)
    {
        if (generation <= 0 || generation != _requestedGeneration || paintedAtTicks <= 0)
            return false;

        _currentPaintedGeneration = generation;
        _currentPaintedAtTicks = paintedAtTicks;
        _lastPaintedGeneration = generation;
        _lastPaintedAtTicks = paintedAtTicks;
        return true;
    }

    internal FirstViewportImagesReadyPaintPollAction ObservePoll(
        long generation,
        bool acknowledgementIsCurrent,
        bool hasVisibleLoadingImages)
    {
        if (generation <= 0 || generation != _requestedGeneration)
            return FirstViewportImagesReadyPaintPollAction.WaitingForPaint;

        if (_currentPaintedGeneration != generation || _currentPaintedAtTicks <= 0)
        {
            // The caller reaches this only after confirming the requested pipeline
            // generation and measuring its current viewport. A missing, rejected, or
            // accepted-but-unrecorded callback may justify one extra invalidation,
            // but none supplies readiness evidence. Wait for visible images before
            // spending that retry.
            bool callbackFailedToEstablishAcknowledgement =
                _lastPaintProbeAccepted is not true ||
                _lastPaintAcknowledgementRecorded is not true;
            if (!hasVisibleLoadingImages &&
                !_paintRearmIssued &&
                callbackFailedToEstablishAcknowledgement)
            {
                _paintRearmIssued = true;
                return FirstViewportImagesReadyPaintPollAction.RearmForCurrentViewport;
            }

            return FirstViewportImagesReadyPaintPollAction.WaitingForPaint;
        }

        // The callback is one-shot. If viewport/revision/scale identity changed after it
        // acknowledged a paint, or visible images became pending again, discard its timestamp
        // and let the caller arm a fresh callback that forces a current-viewport paint.
        if (!acknowledgementIsCurrent || hasVisibleLoadingImages)
        {
            ClearAcknowledgement();
            return FirstViewportImagesReadyPaintPollAction.RearmForCurrentViewport;
        }

        return FirstViewportImagesReadyPaintPollAction.Ready;
    }

    private void ClearAcknowledgement()
    {
        _currentPaintedGeneration = 0;
        _currentPaintedAtTicks = 0;
    }

    private void ClearLastCallbackOutcome()
    {
        _lastPaintProbeAccepted = null;
        _lastPaintAcknowledgementRecorded = null;
    }
}
