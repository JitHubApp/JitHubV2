using System;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Atomic inline vector content. Device-independent commands come from Core;
/// this run alone owns the disposable Win2D geometry built for one layout.
/// </summary>
internal sealed class InlineVectorSceneRun : InlineRun, IDisposable
{
    // A measured-placement clone deliberately has no VectorSceneDrawing. It
    // borrows only the immutable scene value while a private text layout is
    // measured; the published source run retains the sole drawing/native owner.
    private readonly VectorSceneDrawing? _drawing;
    private MarkdownVectorScene? _placementScene;
    private readonly VectorScenePaintPalette _paintPalette;
    private readonly float _sceneWidth;
    private readonly float _sceneHeight;
    private readonly float _sceneBaseline;
    private readonly float _referenceFontSize;
    private readonly double _textScaleFactor;
    private readonly string _capacityFallbackText;
    private readonly Microsoft.UI.Xaml.FlowDirection _flowDirection;
    private Rect _localBounds;
    private bool _disposed;
    private readonly bool _isMeasuredPlacementOnly;

    internal InlineVectorSceneRun(
        MarkdownLayoutContext context,
        MarkdownVectorScene scene,
        string semanticText,
        string accessibilityName,
        string? accessibilityDescription,
        string elementKey,
        VectorNativeResourceAdmission? nativeAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scene);
        _sceneWidth = scene.Width;
        _sceneHeight = scene.Height;
        _sceneBaseline = scene.Baseline;
        _referenceFontSize = scene.ReferenceFontSize;
        _textScaleFactor = context.ThemeSnapshot.TextScaleFactor;
        _flowDirection = context.FlowDirection;
        SemanticText = semanticText ?? string.Empty;
        VectorNativeResourceAdmission? resolvedNativeAdmission =
            VectorSceneCapacityFallback.ResolveNativeAdmission(context, nativeAdmission);
        _capacityFallbackText = resolvedNativeAdmission is null
            ? string.Empty
            : VectorSceneCapacityFallback.CreateInlineText(
                context,
                SemanticText,
                accessibilityName);
        AccessibilityName = accessibilityName ?? string.Empty;
        AccessibilityDescription = accessibilityDescription;
        RenderedLength = 1;
        DesiredWidth = Math.Max(1f, _sceneWidth);
        DesiredHeight = Math.Max(1f, _sceneHeight);
        DesiredBaseline = Math.Clamp(_sceneBaseline, 0f, DesiredHeight);
        string resolvedElementKey = string.IsNullOrWhiteSpace(elementKey)
            ? MarkdownElementKeys.Math
            : elementKey;
        var styleContextKeys = context.CreateStyleContextSnapshot();
        var styleAliasKeys = context.CreateStyleAliasSnapshot();
        ElementStyle vectorStyle = context.ThemeSnapshot.GetStyle(
            resolvedElementKey,
            styleContextKeys,
            styleAliasKeys);
        _paintPalette = new VectorScenePaintPalette(
            vectorStyle.Foreground,
            vectorStyle.Background ?? context.ThemeSnapshot.SurfaceColor,
            context.ThemeSnapshot.GetStyle(
                MarkdownElementKeys.Link,
                styleContextKeys,
                styleAliasKeys).Foreground);
        _drawing = new VectorSceneDrawing(
            context.ResourceCreator,
            scene,
            context.CancellationToken,
            vectorStyle.FontFamily
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            ,
            context.LayoutDiagnostics
#endif
            ,
            deferNativeResources: true,
            nativeAdmission: resolvedNativeAdmission);

        // An eager native factory can fail after creating a prefix of this
        // drawing. Establish construction ownership before preparation, not
        // only after this constructor returns to the builder. Duplicate
        // registration by the builder is harmless in the identity ledger.
        context.RegisterConstructionResource(this);
        if (!ShouldDeferNativeResources(context))
        {
            try
            {
                if (_drawing.NativeResourceAdmission is null)
                    _drawing.PrepareNativeResources(context.CancellationToken);
                else
                    _ = TryPrepareNativeResources(context.CancellationToken, out _);
            }
            catch (Exception preparationFailure)
            {
                // Direct internal callers may not have a construction scope.
                // Give retained partial slots a terminal cleanup attempt while
                // preserving the preparation failure. Scoped production builds
                // still own this run if that cleanup remains incomplete.
                try
                {
                    _drawing.Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(
                        "Inline vector preparation failed and retirement was incomplete.",
                        preparationFailure, cleanupFailure);
                }
                throw;
            }
        }
    }

    private InlineVectorSceneRun(InlineVectorSceneRun source)
        : base(source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(source._disposed, source);
        if (source._isMeasuredPlacementOnly)
            throw new InvalidOperationException("A measured-placement vector cannot be cloned again.");

        _isMeasuredPlacementOnly = true;
        _placementScene = source.Scene;
        _paintPalette = source._paintPalette;
        _sceneWidth = source._sceneWidth;
        _sceneHeight = source._sceneHeight;
        _sceneBaseline = source._sceneBaseline;
        _referenceFontSize = source._referenceFontSize;
        _textScaleFactor = source._textScaleFactor;
        _flowDirection = source._flowDirection;
        _capacityFallbackText = string.Empty;
        SemanticText = source.SemanticText;
        AccessibilityName = source.AccessibilityName;
        AccessibilityDescription = source.AccessibilityDescription;
        DeferredSceneMarker = source.DeferredSceneMarker;
        DeferredSceneBinding = source.DeferredSceneBinding;
        LinkUrl = source.LinkUrl;
        LinkTitle = source.LinkTitle;
        DesiredWidth = source.DesiredWidth;
        DesiredHeight = source.DesiredHeight;
        DesiredBaseline = source.DesiredBaseline;
        _localBounds = source._localBounds;
        RenderedLength = source.RenderedLength;
    }

    /// <summary>
    /// Creates a non-paintable run recipe for bounded root-local measurement.
    /// It shares the immutable scene, but never the source drawing or any
    /// native resources. Whole-tree COW must continue to reject this type.
    /// </summary>
    internal InlineVectorSceneRun CloneForMeasuredPlacement() => new(this);

    public override string Text => InlineEmbedRun.PlaceholderChar;

    public override string AccessibleText => SemanticText;

    internal readonly string SemanticText;

    internal readonly string AccessibilityName;

    internal readonly string? AccessibilityDescription;

    internal string? DeferredSceneMarker { get; init; }

    internal MarkdownDeferredSceneBinding? DeferredSceneBinding { get; init; }

    internal string? LinkUrl { get; set; }

    internal string? LinkTitle { get; set; }

    internal bool IsLinked => !string.IsNullOrWhiteSpace(LinkUrl);

    internal float DesiredWidth { get; private set; }

    internal float DesiredHeight { get; private set; }

    internal float DesiredBaseline { get; private set; }

    internal Rect LocalBounds => _localBounds;

    internal bool IsMeasuredPlacementOnly => _isMeasuredPlacementOnly;

    internal bool IsDisposed => _disposed;

    internal MarkdownVectorScene Scene => _placementScene ??
        _drawing?.Scene ?? throw new ObjectDisposedException(nameof(InlineVectorSceneRun));

    internal bool IsNativeResourcesPrepared => !_isMeasuredPlacementOnly &&
        _drawing?.IsPrepared == true;

    internal VectorNativeResourceAdmission? NativeAdmission =>
        !_isMeasuredPlacementOnly ? _drawing?.NativeResourceAdmission : null;

    internal VectorNativePreparationStatus NativePreparationStatus =>
        !_isMeasuredPlacementOnly
            ? _drawing?.NativePreparationStatus ?? VectorNativePreparationStatus.NotPrepared
            : VectorNativePreparationStatus.NotPrepared;

    internal bool IsCapacityFallbackPaintable => !_isMeasuredPlacementOnly && !_disposed &&
        _capacityFallbackText.Length > 0 &&
        (NativePreparationStatus is VectorNativePreparationStatus.DeferredCapacity or
            VectorNativePreparationStatus.TooLarge) &&
        VectorSceneCapacityFallback.TryGetFormat(_flowDirection, out _);

    internal void PrepareNativeResources(CancellationToken cancellationToken)
    {
        if (_isMeasuredPlacementOnly)
            throw new InvalidOperationException("A measured-placement vector cannot prepare native resources.");
        _ = TryPrepareNativeResources(cancellationToken, out _);
    }

    internal bool TryPrepareNativeResources(
        CancellationToken cancellationToken,
        out VectorNativePreparationOutcome outcome)
    {
        if (_isMeasuredPlacementOnly)
            throw new InvalidOperationException("A measured-placement vector cannot prepare native resources.");
        bool prepared = _drawing!.TryPrepareNativeResources(cancellationToken, out outcome);
        if (outcome.Status is VectorNativePreparationStatus.DeferredCapacity or VectorNativePreparationStatus.TooLarge)
            _ = PrepareCapacityFallback(cancellationToken);
        return prepared;
    }

    internal bool PrepareCapacityFallback(CancellationToken cancellationToken)
    {
        if (_isMeasuredPlacementOnly || _disposed || _drawing?.NativeResourceAdmission is null ||
            _capacityFallbackText.Length == 0)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Keep capacity/size denial as the typed fallback result. A real text
        // descriptor/device failure is not capacity pressure; let the existing
        // renderer recovery path observe it instead of settling an unpaintable run.
        return VectorSceneCapacityFallback.PrepareFormat(_flowDirection);
    }

    internal void ReleaseNativeResources()
    {
        if (!_isMeasuredPlacementOnly)
            _drawing?.ReleaseNativeResources();
    }

    internal void Measure(float fontSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        float scale = _referenceFontSize > 0
            ? Math.Max(0.01f, fontSize / _referenceFontSize)
            : 1f;
        DesiredWidth = Math.Max(1f, _sceneWidth * scale);
        DesiredHeight = Math.Max(1f, _sceneHeight * scale);
        DesiredBaseline = Math.Clamp(_sceneBaseline * scale, 0f, DesiredHeight);
    }

    /// <summary>
    /// Verifies the scalar result of <see cref="Measure(float)"/> without
    /// mutating the run or touching its drawing/native resources.
    /// </summary>
    internal bool HasExpectedMeasuredDimensions(float fontSize)
    {
        if (_disposed || !float.IsFinite(fontSize) || fontSize <= 0f)
            return false;

        float scale = _referenceFontSize > 0
            ? Math.Max(0.01f, fontSize / _referenceFontSize)
            : 1f;
        float width = Math.Max(1f, _sceneWidth * scale);
        float height = Math.Max(1f, _sceneHeight * scale);
        float baseline = Math.Clamp(_sceneBaseline * scale, 0f, height);
        return float.IsFinite(width) && width > 0f &&
            float.IsFinite(height) && height > 0f &&
            float.IsFinite(baseline) && baseline >= 0f && baseline <= height &&
            DesiredWidth == width && DesiredHeight == height && DesiredBaseline == baseline;
    }

    internal void SetLocalBounds(Rect bounds) => _localBounds = bounds;

    internal void RestoreLayoutState(
        float desiredWidth,
        float desiredHeight,
        float desiredBaseline,
        Rect localBounds)
    {
        DesiredWidth = desiredWidth;
        DesiredHeight = desiredHeight;
        DesiredBaseline = desiredBaseline;
        _localBounds = localBounds;
    }

    internal void Paint(
        CanvasDrawingSession drawingSession,
        float baseX,
        float baseY,
        Color semanticForeground,
        bool highContrast,
        Color? foregroundOverride = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isMeasuredPlacementOnly)
            throw new InvalidOperationException("A measured-placement vector cannot be painted.");
        Rect bounds = new(
            baseX + _localBounds.X,
            baseY + _localBounds.Y,
            _localBounds.Width,
            _localBounds.Height);
        if (_drawing?.IsPrepared == true)
        {
            _drawing.Paint(
                drawingSession,
                bounds,
                _paintPalette with { Foreground = semanticForeground },
                highContrast,
                foregroundOverride);
            return;
        }

        if (!IsCapacityFallbackPaintable || bounds.Width <= 0 || bounds.Height <= 0 ||
            !VectorSceneCapacityFallback.TryGetFormat(_flowDirection, out CanvasTextFormat format))
        {
            return;
        }

        NativePaintSessionResources.LayerLease fallbackLayer =
            NativePaintSessionResources.CreateLayer(drawingSession, 1f, bounds);
        try
        {
            VectorSceneCapacityFallback.DrawTextWithScale(
                drawingSession,
                _capacityFallbackText,
                bounds,
                foregroundOverride ?? semanticForeground,
                format,
                _textScaleFactor);
        }
        catch (Exception primary)
        {
            fallbackLayer.ClosePreservingPrimary(primary);
            throw;
        }
        fallbackLayer.Close();
    }

    public void Dispose()
    {
        if (_isMeasuredPlacementOnly && _disposed)
            return;

        // Semantic retirement is terminal, but a native cleanup failure must
        // still allow a later owner retirement to retry the remaining slots.
        _disposed = true;
        if (_isMeasuredPlacementOnly)
        {
            // No native owner exists on this path. Drop the borrowed immutable
            // graph so an abandoned private candidate cannot extend its life.
            _placementScene = null;
            return;
        }

        _drawing?.Dispose();
    }

    private static bool ShouldDeferNativeResources(MarkdownLayoutContext context)
    {
        IMarkdownPerformanceSessionInternal? session = context.PerformanceSession;
        return context.UseProgressiveNativeSceneResidency &&
            session is not null &&
            !session.IsDisposed &&
            session.Options.DeferOffscreenScenes;
    }
}
