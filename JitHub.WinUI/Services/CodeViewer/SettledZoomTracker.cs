using System;

namespace JitHub.Services.CodeViewer;

internal sealed class SettledZoomTracker
{
    private const float ZoomEpsilon = 0.001f;

    private float _lastReportedZoomFactor = 1;
    private float? _pendingZoomFactor;

    public void Reset(float zoomFactor)
    {
        _lastReportedZoomFactor = IsValid(zoomFactor) ? zoomFactor : 1;
        _pendingZoomFactor = null;
    }

    public bool Observe(float zoomFactor)
    {
        if (!IsValid(zoomFactor) ||
            Math.Abs(zoomFactor - _lastReportedZoomFactor) <= ZoomEpsilon)
        {
            _pendingZoomFactor = null;
            return false;
        }

        _pendingZoomFactor = zoomFactor;
        return true;
    }

    public bool TrySettle(out float zoomFactor)
    {
        float? pendingZoomFactor = _pendingZoomFactor;
        _pendingZoomFactor = null;
        if (pendingZoomFactor is not float pending ||
            Math.Abs(pending - _lastReportedZoomFactor) <= ZoomEpsilon)
        {
            zoomFactor = _lastReportedZoomFactor;
            return false;
        }

        _lastReportedZoomFactor = pending;
        zoomFactor = pending;
        return true;
    }

    public void ClearPending() => _pendingZoomFactor = null;

    private static bool IsValid(float zoomFactor) =>
        float.IsFinite(zoomFactor) && zoomFactor > 0;
}
