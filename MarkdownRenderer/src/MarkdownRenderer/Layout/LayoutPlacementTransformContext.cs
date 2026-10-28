using System;
using System.Numerics;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Carries the immutable root-placement translation through synchronous box
/// geometry queries. Inline text must snap against its final logical origin,
/// even while the box itself still owns root-local Bounds. The scope is
/// thread-local, nestable, value-only, and deliberately contains no await point.
/// </summary>
internal static class LayoutPlacementTransformContext
{
    [ThreadStatic]
    private static float _translationX;

    [ThreadStatic]
    private static float _translationY;

    [ThreadStatic]
    private static int _scopeToken;

    [ThreadStatic]
    private static int _nextScopeToken;

    [ThreadStatic]
    private static Matrix3x2 _baseCanvasTransform;

    [ThreadStatic]
    private static bool _hasBaseCanvasTransform;

    internal static Scope Enter(float translationX, float translationY)
        => EnterCore(translationX, translationY, hasBaseCanvasTransform: false, default);

    internal static Scope Enter(
        float translationX,
        float translationY,
        Matrix3x2 baseCanvasTransform)
        => EnterCore(translationX, translationY, hasBaseCanvasTransform: true, baseCanvasTransform);

    private static Scope EnterCore(
        float translationX,
        float translationY,
        bool hasBaseCanvasTransform,
        Matrix3x2 baseCanvasTransform)
    {
        if (!float.IsFinite(translationX) || !float.IsFinite(translationY))
            throw new ArgumentOutOfRangeException(nameof(translationX), "Placement translation must be finite.");

        float previousX = _translationX;
        float previousY = _translationY;
        float nextX = previousX + translationX;
        float nextY = previousY + translationY;
        if (!float.IsFinite(nextX) || !float.IsFinite(nextY))
            throw new ArgumentOutOfRangeException(nameof(translationX), "Accumulated placement translation must be finite.");

        int previousToken = _scopeToken;
        Matrix3x2 previousBaseCanvasTransform = _baseCanvasTransform;
        bool previousHasBaseCanvasTransform = _hasBaseCanvasTransform;
        int token = unchecked(_nextScopeToken + 1);
        if (token == 0)
            token = 1;
        _nextScopeToken = token;
        _translationX = nextX;
        _translationY = nextY;
        if (hasBaseCanvasTransform)
        {
            _baseCanvasTransform = baseCanvasTransform;
            _hasBaseCanvasTransform = true;
        }
        _scopeToken = token;
        return new Scope(previousX, previousY, previousToken, token,
            previousBaseCanvasTransform, previousHasBaseCanvasTransform);
    }

    /// <summary>
    /// Snaps a root-local origin so that applying the active placement
    /// translation produces the same device-pixel position as a fresh layout
    /// at that final logical origin.
    /// </summary>
    internal static void SnapOriginToDevicePixels(
        float rootLocalX,
        float rootLocalY,
        float scale,
        out float snappedRootLocalX,
        out float snappedRootLocalY)
    {
        if (!float.IsFinite(scale) || scale <= 0f)
            scale = 1f;

        float translationX = _translationX;
        float translationY = _translationY;
        snappedRootLocalX = MathF.Round((rootLocalX + translationX) * scale) / scale - translationX;
        snappedRootLocalY = MathF.Round((rootLocalY + translationY) * scale) / scale - translationY;
    }

    internal static void SnapFinalOriginToDevicePixels(
        float rootLocalX,
        float rootLocalY,
        float scale,
        out float snappedX,
        out float snappedY)
    {
        if (!float.IsFinite(scale) || scale <= 0f)
            scale = 1f;

        snappedX = MathF.Round((rootLocalX + _translationX) * scale) / scale;
        snappedY = MathF.Round((rootLocalY + _translationY) * scale) / scale;
    }

    /// <summary>
    /// Removes only the active root-placement prefix from a canvas transform.
    /// DrawTextLayout is then submitted at its final snapped logical origin,
    /// keeping DirectWrite's effective transform identical to fresh layout.
    /// </summary>
    internal static Matrix3x2 RemoveCurrentPlacementTranslation(Matrix3x2 transform)
    {
        float translationX = _translationX;
        float translationY = _translationY;
        return translationX == 0f && translationY == 0f
            ? transform
            : Matrix3x2.CreateTranslation(-translationX, -translationY) * transform;
    }

    internal static bool TryGetBaseCanvasTransform(out Matrix3x2 transform)
    {
        transform = _baseCanvasTransform;
        return _hasBaseCanvasTransform;
    }

    /// <summary>
    /// Gets the canvas transform without the active root-placement translation.
    /// At a top-level paint boundary this is the exact caller matrix captured
    /// before placement was applied. A nested drawing path may provide its own
    /// transform composition on top of that matrix and should carry this base
    /// forward alongside that composition rather than trying to invert it.
    /// </summary>
    internal static bool TryGetPlacementFreeCanvasTransform(
        Matrix3x2 currentTransform,
        out Matrix3x2 transform)
    {
        if (_translationX == 0f && _translationY == 0f)
        {
            transform = currentTransform;
            return false;
        }

        transform = _hasBaseCanvasTransform
            ? _baseCanvasTransform
            : RemoveCurrentPlacementTranslation(currentTransform);
        return true;
    }

    internal static void GetCurrentTranslation(out float translationX, out float translationY)
    {
        translationX = _translationX;
        translationY = _translationY;
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly float _previousX;
        private readonly float _previousY;
        private readonly int _previousToken;
        private readonly int _token;
        private readonly Matrix3x2 _previousBaseCanvasTransform;
        private readonly bool _previousHasBaseCanvasTransform;

        internal Scope(
            float previousX,
            float previousY,
            int previousToken,
            int token,
            Matrix3x2 previousBaseCanvasTransform,
            bool previousHasBaseCanvasTransform)
        {
            _previousX = previousX;
            _previousY = previousY;
            _previousToken = previousToken;
            _token = token;
            _previousBaseCanvasTransform = previousBaseCanvasTransform;
            _previousHasBaseCanvasTransform = previousHasBaseCanvasTransform;
        }

        public void Dispose()
        {
            // The token makes copied/double-disposed scopes harmless and keeps
            // a misplaced outer Dispose from erasing a still-active inner one.
            if (_scopeToken != _token)
                return;

            _translationX = _previousX;
            _translationY = _previousY;
            _baseCanvasTransform = _previousBaseCanvasTransform;
            _hasBaseCanvasTransform = _previousHasBaseCanvasTransform;
            _scopeToken = _previousToken;
        }
    }
}
