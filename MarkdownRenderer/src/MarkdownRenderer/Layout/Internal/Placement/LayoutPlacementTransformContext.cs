using System;
using System.Numerics;
using MarkdownRenderer.Layout.Internal.Context;

namespace MarkdownRenderer.Layout.Internal.Placement;

/// <summary>
/// Carries the immutable root-placement translation through synchronous box
/// geometry queries. Inline text must snap against its final logical origin,
/// even while the box itself still owns root-local Bounds. Scopes are
/// nestable, value-only, and deliberately contain no await point.
/// </summary>
/// <remarks>
/// Each <see cref="MarkdownLayoutContext"/> owns one instance, which its boxes
/// read. A placement clone gets a fresh context and so a fresh instance. Only
/// the pass that owns the context enters a scope: the builder or a clone's
/// measurement before publication, and afterwards the holder of the snapshot
/// layout gate, which paint, hit testing and every geometry read take.
/// </remarks>
internal sealed class LayoutPlacementTransformContext
{
    private float _translationX;
    private float _translationY;
    private int _scopeToken;
    private int _nextScopeToken;
    private Matrix3x2 _baseCanvasTransform;
    private bool _hasBaseCanvasTransform;

    internal Scope Enter(float translationX, float translationY)
        => EnterCore(translationX, translationY, hasBaseCanvasTransform: false, default);

    internal Scope Enter(
        float translationX,
        float translationY,
        Matrix3x2 baseCanvasTransform)
        => EnterCore(translationX, translationY, hasBaseCanvasTransform: true, baseCanvasTransform);

    private Scope EnterCore(
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
        return new Scope(this, previousX, previousY, previousToken, token,
            previousBaseCanvasTransform, previousHasBaseCanvasTransform);
    }

    /// <summary>
    /// Snaps a root-local origin so that applying the active placement
    /// translation produces the same device-pixel position as a fresh layout
    /// at that final logical origin.
    /// </summary>
    internal void SnapOriginToDevicePixels(
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

    internal void SnapFinalOriginToDevicePixels(
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
    internal Matrix3x2 RemoveCurrentPlacementTranslation(Matrix3x2 transform)
    {
        float translationX = _translationX;
        float translationY = _translationY;
        return translationX == 0f && translationY == 0f
            ? transform
            : Matrix3x2.CreateTranslation(-translationX, -translationY) * transform;
    }

    internal bool TryGetBaseCanvasTransform(out Matrix3x2 transform)
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
    internal bool TryGetPlacementFreeCanvasTransform(
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

    internal void GetCurrentTranslation(out float translationX, out float translationY)
    {
        translationX = _translationX;
        translationY = _translationY;
    }

    private void Exit(
        float previousX,
        float previousY,
        int previousToken,
        int token,
        Matrix3x2 previousBaseCanvasTransform,
        bool previousHasBaseCanvasTransform)
    {
        // The token makes copied/double-disposed scopes harmless and keeps
        // a misplaced outer Dispose from erasing a still-active inner one.
        if (_scopeToken != token)
            return;

        _translationX = previousX;
        _translationY = previousY;
        _baseCanvasTransform = previousBaseCanvasTransform;
        _hasBaseCanvasTransform = previousHasBaseCanvasTransform;
        _scopeToken = previousToken;
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly LayoutPlacementTransformContext? _owner;
        private readonly float _previousX;
        private readonly float _previousY;
        private readonly int _previousToken;
        private readonly int _token;
        private readonly Matrix3x2 _previousBaseCanvasTransform;
        private readonly bool _previousHasBaseCanvasTransform;

        internal Scope(
            LayoutPlacementTransformContext owner,
            float previousX,
            float previousY,
            int previousToken,
            int token,
            Matrix3x2 previousBaseCanvasTransform,
            bool previousHasBaseCanvasTransform)
        {
            _owner = owner;
            _previousX = previousX;
            _previousY = previousY;
            _previousToken = previousToken;
            _token = token;
            _previousBaseCanvasTransform = previousBaseCanvasTransform;
            _previousHasBaseCanvasTransform = previousHasBaseCanvasTransform;
        }

        public void Dispose() => _owner?.Exit(
            _previousX,
            _previousY,
            _previousToken,
            _token,
            _previousBaseCanvasTransform,
            _previousHasBaseCanvasTransform);
    }
}
