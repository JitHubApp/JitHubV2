using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Owns device-dependent resources for one immutable vector scene. Both inline
/// formulas and block diagrams use this implementation so their paint behavior
/// cannot drift.
/// </summary>
internal sealed class VectorSceneDrawing : IDisposable
{
    // DirectWrite rejects extremely large requested layout dimensions even
    // when they are finite.  float.MaxValue also makes its internal glyph
    // arithmetic overflow for sufficiently long labels.  Vector scenes are
    // already bounded, so use a generous finite canvas instead of pretending
    // that the text has an infinite layout surface.
    private const float MaximumTextLayoutDimension = 1_000_000f;

    private ICanvasResourceCreator? _resourceCreator;
    private NativeResourceRetirementRecord? _nativeRetirement;
    private readonly NativeResourceRetirement? _nativeRetirementOwner;
    private readonly VectorNativeResourceAdmission? _nativeAdmission;
    private VectorNativeResourceCredit? _nativeCredit;
    private volatile VectorNativePreparationStatus _nativePreparationStatus;
    private long _nativeResourceWeightUnits;
    private VectorNativePreparationOutcome _preparationOutcome;
    private Action<IDisposable>? _nativeDisposer;
    private Func<CanvasDrawingSession, CanvasGeometry, CanvasActiveLayer>? _testClipFactory;
    private MarkdownVectorScene? _scene;
    private readonly string? _hostFontFamily;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private readonly MarkdownLayoutDiagnostics? _layoutDiagnostics;
#endif
    private CanvasGeometry?[]? _geometries;
    private CanvasTextLayout?[]? _textLayouts;
    private CanvasStrokeStyle?[]? _strokeStyles;
    private CanvasTextFormat? _pendingTextFormat;
    private CanvasPathBuilder? _pendingPathBuilder;
    private bool[]? _linkedSemantics;
    private Matrix3x2[]? _transformStack;
    private CanvasActiveLayer?[]? _clipStack;
    private NativeResourceShape? _nativeResourceShape;
    private volatile bool _resourcesPrepared;
    private volatile bool _resourceCleanupPending;
    private volatile bool _clipCleanupFailed;
    private volatile bool _disposed;

    internal VectorSceneDrawing(
        ICanvasResourceCreator resourceCreator,
        MarkdownVectorScene scene,
        CancellationToken cancellationToken,
        string? hostFontFamily = null
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        ,
        MarkdownLayoutDiagnostics? layoutDiagnostics = null
#endif
        ,
        bool deferNativeResources = false,
        NativeResourceRetirement? nativeRetirement = null,
        Action<IDisposable>? nativeDisposer = null,
        VectorNativeResourceAdmission? nativeAdmission = null)
    {
        _resourceCreator = resourceCreator ?? throw new ArgumentNullException(nameof(resourceCreator));
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _hostFontFamily = hostFontFamily;
        if (nativeDisposer is not null && nativeRetirement?.AllowsTestDisposer != true)
            throw new ArgumentException("Native disposal fault plans require an isolated retirement owner.", nameof(nativeDisposer));
        _nativeRetirementOwner = nativeRetirement;
        _nativeAdmission = nativeAdmission;
        _nativeDisposer = nativeDisposer;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        _layoutDiagnostics = layoutDiagnostics;
#endif

        if (nativeAdmission is not null && !deferNativeResources)
        {
            throw new ArgumentException(
                "Aggregate admission requires deferred construction so capacity denial can be handled explicitly.",
                nameof(deferNativeResources));
        }

        if (!deferNativeResources)
        {
            try
            {
                PrepareNativeResources(cancellationToken);
            }
            catch (Exception preparationFailure)
            {
                // A constructor that throws has no caller-owned drawing. Its
                // terminal transfer must happen here, not depend on a scope
                // receiving the never-returned object.
                try { Dispose(); }
                catch (Exception retirementFailure)
                {
                    throw new AggregateException(
                        "Vector construction failed and retirement was incomplete.",
                        preparationFailure, retirementFailure);
                }
                throw;
            }
        }
    }

    internal MarkdownVectorScene Scene => GetScene();

    internal bool HasScene => _scene is not null;

    internal bool IsPrepared => _resourcesPrepared;

    internal VectorNativePreparationStatus NativePreparationStatus => _nativePreparationStatus;

    internal VectorNativePreparationStatus PreparationStatus => _nativePreparationStatus;

    internal long NativeResourceWeightUnits => _nativeResourceWeightUnits;

    internal VectorNativeResourceAdmission? NativeResourceAdmission => _nativeAdmission;

    internal VectorNativeResourceAdmission? NativeAdmission => _nativeAdmission;

    internal VectorNativePreparationOutcome PreparationOutcome => _preparationOutcome;

    internal void SetClipFactoryForTest(Func<CanvasDrawingSession, CanvasGeometry, CanvasActiveLayer> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_nativeRetirementOwner?.AllowsTestDisposer != true)
            throw new InvalidOperationException("Native clip factory fault plans require an isolated retirement owner.");
        _testClipFactory = factory;
    }

    /// <summary>
    /// Drops the immutable command graph after reversible native resources have
    /// been released. A failed native cleanup leaves the graph attached so its
    /// owner remains valid and can retry or retire it terminally.
    /// </summary>
    internal void DetachSceneGraph()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_clipCleanupFailed)
            throw new InvalidOperationException("A vector drawing with a failed active-layer close cannot be prepared again.");
        if (_scene is null)
            return;

        ReleaseNativeResources();
        _scene = null;
    }

    internal void PrepareNativeResources(CancellationToken cancellationToken)
        => _ = TryPrepareNativeResources(cancellationToken, out _);

    internal bool TryPrepareNativeResources(
        CancellationToken cancellationToken,
        out VectorNativePreparationOutcome outcome)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_clipCleanupFailed)
            throw new InvalidOperationException("A vector drawing with a failed active-layer close cannot be prepared again.");
        if (_resourceCleanupPending)
            ReleaseNativeResources();
        if (_resourcesPrepared)
        {
            _nativePreparationStatus = VectorNativePreparationStatus.Prepared;
            outcome = new(_nativePreparationStatus, _nativeResourceWeightUnits, _nativeAdmission?.CapacityGeneration ?? 0);
            _preparationOutcome = outcome;
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();

        MarkdownVectorScene scene = GetScene();
        if (_nativeResourceShape is not { } shape)
        {
            shape = CountNativeResourceShape(scene, cancellationToken, calculateWeight: _nativeAdmission is not null);
            // Immutable command graphs are not rescanned during ordinary
            // viewport-band exchanges while their drawing owner remains live.
            _nativeResourceShape = shape;
        }
        _nativeResourceWeightUnits = shape.WeightUnits;

        if (_nativeAdmission is not null)
        {
            VectorNativeAdmissionResult admission = _nativeAdmission.TryReserve(
                shape.WeightUnits,
                out VectorNativeResourceCredit? credit,
                out long observedGeneration);
            if (admission != VectorNativeAdmissionResult.Granted)
            {
                _nativePreparationStatus = admission == VectorNativeAdmissionResult.DeferredCapacity
                    ? VectorNativePreparationStatus.DeferredCapacity
                    : VectorNativePreparationStatus.TooLarge;
                outcome = new(_nativePreparationStatus, shape.WeightUnits, observedGeneration);
                _preparationOutcome = outcome;
                return false;
            }
            _nativeCredit = credit;
        }

        try
        {
            PrepareNativeResourcesCore(scene, shape, cancellationToken);
            _nativePreparationStatus = VectorNativePreparationStatus.Prepared;
            outcome = new(_nativePreparationStatus, shape.WeightUnits, _nativeAdmission?.CapacityGeneration ?? 0);
            _preparationOutcome = outcome;
            return true;
        }
        catch
        {
            outcome = new(VectorNativePreparationStatus.NotPrepared, shape.WeightUnits, _nativeAdmission?.CapacityGeneration ?? 0);
            _preparationOutcome = outcome;
            throw;
        }
    }

    private void PrepareNativeResourcesCore(
        MarkdownVectorScene scene,
        NativeResourceShape shape,
        CancellationToken cancellationToken)
    {
        bool retirementRecordCreated = false;
        try
        {
            // Cancellation can race the successful aggregate reservation. Do
            // not allocate cleanup custody or reach a native factory when the
            // owner has already canceled; the catch below returns the credit.
            cancellationToken.ThrowIfCancellationRequested();

            // The retirement record and every managed slot array are reserved
            // before the first native factory. If managed setup fails here,
            // no uncertain native owner exists and the ledger credit refunds.
            if (_nativeRetirement is null)
            {
                _nativeRetirement = (_nativeRetirementOwner ?? NativeResourceRetirement.Shared)
                    .Reserve(disposer: _nativeDisposer);
                retirementRecordCreated = true;
            }

            PrepareNativeResourcesWithReservedCustody(scene, shape, cancellationToken);
        }
        catch
        {
            if (!_resourceCleanupPending && !_resourcesPrepared)
            {
                if (retirementRecordCreated)
                    _nativeRetirement = null;
                RefundNativeCredit();
            }
            throw;
        }
    }

    private void PrepareNativeResourcesWithReservedCustody(
        MarkdownVectorScene scene,
        NativeResourceShape shape,
        CancellationToken cancellationToken)
    {
        // A rectangle/line command needs none of these native slots. Path and
        // text commands need just one kind. Do not retain five command-sized
        // scratch arrays per drawing, especially for large sparse diagrams.
        CanvasGeometry?[] geometries = CreateSlots<CanvasGeometry>(shape.Geometries);
        CanvasTextLayout?[] textLayouts = CreateSlots<CanvasTextLayout>(shape.TextLayouts);
        CanvasStrokeStyle?[] strokeStyles = CreateSlots<CanvasStrokeStyle>(shape.StrokeStyles);
        var linkedSemantics = new bool[scene.Semantics.Count];
        Matrix3x2[] transformStack = shape.TransformDepth == 0
            ? Array.Empty<Matrix3x2>()
            : new Matrix3x2[shape.TransformDepth];
        CanvasActiveLayer?[] clipStack = CreateSlots<CanvasActiveLayer>(shape.ClipDepth);
        int geometryIndex = 0;
        int textIndex = 0;
        int strokeIndex = 0;

        for (int i = 0; i < scene.Links.Count; i++)
            linkedSemantics[scene.Links[i].SemanticIndex] = true;

        try
        {
            for (int i = 0; i < scene.Commands.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MarkdownVectorCommand command = scene.Commands[i];
                if (RequiresGeometry(command.Kind))
                {
                    CreateGeometry(
                        ref geometries[geometryIndex++],
                        _resourceCreator!,
                        command.Path,
                        command.Style?.FillRule ?? MarkdownVectorFillRule.Nonzero,
                        cancellationToken);
                }

                if (RequiresTextLayout(command))
                {
                    string text = command.Text!;
                    MarkdownVectorTextStyle textStyle = command.TextStyle!;
                    int textSlot = textIndex++;
                    CanvasTextFormat format = _pendingTextFormat = new CanvasTextFormat();
                    Exception? textFailure = null;
                    try
                    {
                        format.FontFamily = ResolveFontFamily(textStyle, _hostFontFamily);
                        format.FontSize = textStyle.FontSize;
                        format.FontWeight = new FontWeight { Weight = textStyle.FontWeight };
                        format.FontStyle = textStyle.Italic ? FontStyle.Italic : FontStyle.Normal;
                        format.Direction = textStyle.RightToLeft
                            ? CanvasTextDirection.RightToLeftThenTopToBottom
                            : CanvasTextDirection.LeftToRightThenTopToBottom;
                        format.WordWrapping = CanvasWordWrapping.NoWrap;

                        (float layoutWidth, float layoutHeight) = ResolveTextLayoutSize(
                            scene,
                            text,
                            textStyle);
                        try
                        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                            textLayouts[textSlot] = CanvasTextLayoutFactory.Create(
                                _resourceCreator!,
                                text,
                                format,
                                layoutWidth,
                                layoutHeight,
                                _layoutDiagnostics);
#else
                            textLayouts[textSlot] = new CanvasTextLayout(
                                _resourceCreator!,
                                text,
                                format,
                                layoutWidth,
                                layoutHeight);
#endif
                        }
                        catch (Exception exception) when (
                            exception is ArgumentException or COMException)
                        {
                            throw new ArgumentException(
                                $"Win2D rejected vector text command {i} " +
                                $"(length={text.Length}, family='{format.FontFamily}', " +
                                $"size={format.FontSize}, weight={format.FontWeight.Weight}, " +
                                $"layout={layoutWidth}x{layoutHeight}).",
                                exception);
                        }
                    }
                    catch (Exception exception)
                    {
                        textFailure = exception;
                    }

                    Exception? formatCleanupFailure = null;
                    try
                    {
                        BlockBoxCleanupScope cleanup = default;
                        cleanup.TryDisposeAndClear(ref _pendingTextFormat, NativeDisposer);
                        cleanup.ThrowIfAny("disposing vector text format");
                    }
                    catch (Exception exception)
                    {
                        formatCleanupFailure = exception;
                    }

                    if (textFailure is not null)
                    {
                        if (formatCleanupFailure is not null)
                        {
                            throw new AggregateException(
                                "Vector text preparation and temporary format cleanup both failed.",
                                textFailure,
                                formatCleanupFailure);
                        }

                        ExceptionDispatchInfo.Capture(textFailure).Throw();
                    }

                    if (formatCleanupFailure is not null)
                        ExceptionDispatchInfo.Capture(formatCleanupFailure).Throw();
                }

                if (command.Style is { } style && RequiresStrokeStyle(style))
                {
                    // Publish the native owner before property setters or a
                    // custom dash-array copy can fail.
                    int strokeSlot = strokeIndex++;
                    strokeStyles[strokeSlot] = new CanvasStrokeStyle();
                    ConfigureStrokeStyle(strokeStyles[strokeSlot]!, style);
                }
            }
        }
        catch (Exception preparationException)
        {
            // Publish ownership before attempting cleanup. If a native Dispose
            // fails, ReleaseNativeResources retains only the failed slots so
            // this drawing remains the retry owner instead of losing local
            // arrays as the stack unwinds.
            _geometries = geometries;
            _textLayouts = textLayouts;
            _strokeStyles = strokeStyles;
            _clipStack = clipStack;
            _linkedSemantics = null;
            _transformStack = null;
            _resourcesPrepared = false;
            _resourceCleanupPending = true;

            try
            {
                ReleaseNativeResources();
            }
            catch (Exception cleanupException)
            {
                // The primary preparation failure and the bounded cleanup
                // summary both matter. BlockBoxCleanupScope bounds the latter's
                // retained exception details before it reaches this aggregate.
                throw new AggregateException(
                    "Vector native preparation failed and cleanup was incomplete.",
                    preparationException,
                    cleanupException);
            }

            throw;
        }

        _geometries = geometries;
        _textLayouts = textLayouts;
        _strokeStyles = strokeStyles;
        _linkedSemantics = linkedSemantics;
        _transformStack = transformStack;
        _clipStack = clipStack;
        _resourcesPrepared = true;
        _resourceCleanupPending = false;
    }

    internal void ReleaseNativeResources()
    {
        if (!_resourcesPrepared && !_resourceCleanupPending)
            return;

        // Prevent Paint from treating a partially released graph as ready.
        // Keep resource arrays attached while cleanup runs: successful slots
        // are cleared individually and throwing slots remain available for a
        // later retry.
        _resourcesPrepared = false;
        _resourceCleanupPending = true;
        _linkedSemantics = null;
        _transformStack = null;
        try
        {
            DisposeDeviceResources(
                ref _geometries,
                ref _textLayouts,
                ref _strokeStyles,
                ref _clipStack,
                ref _pendingTextFormat,
                ref _pendingPathBuilder,
                NativeDisposer,
                preserveGeometryForUncertainClips: _clipCleanupFailed);
            _resourceCleanupPending = false;
            _nativePreparationStatus = VectorNativePreparationStatus.NotPrepared;
            _preparationOutcome = new(_nativePreparationStatus, _nativeResourceWeightUnits, _nativeAdmission?.CapacityGeneration ?? 0);
            RefundNativeCredit();
        }
        catch
        {
            // Failed slots remain on this object; successful slots and fully
            // drained arrays have already been cleared by the helper.
            _resourceCleanupPending = true;
            throw;
        }
    }

    internal void Paint(
        CanvasDrawingSession drawingSession,
        Rect destination,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MarkdownVectorScene scene = GetScene();
        if (!_resourcesPrepared ||
            _geometries is not { } geometries ||
            _textLayouts is not { } textLayouts ||
            _strokeStyles is not { } strokeStyles ||
            _transformStack is not { } transformStack ||
            _clipStack is not { } clipStack)
        {
            // Lazy layout may retire an offscreen scene's native resources.
            // The owning LayoutSnapshot keeps drawing and retirement mutually
            // exclusive; an unprepared scene is a readiness miss, not a paint
            // callback's invitation to create geometry or text layouts.
            return;
        }

        if (destination.Width <= 0 || destination.Height <= 0 ||
            scene.Viewport.Width <= 0 || scene.Viewport.Height <= 0)
        {
            return;
        }

        Matrix3x2 callerTransform = drawingSession.Transform;
        bool hasPlacement = LayoutPlacementTransformContext.TryGetPlacementFreeCanvasTransform(
            callerTransform,
            out Matrix3x2 placementFreeCallerTransform);
        if (hasPlacement)
        {
            LayoutPlacementTransformContext.GetCurrentTranslation(
                out float placementX,
                out float placementY);
            destination = new Rect(
                destination.X + placementX,
                destination.Y + placementY,
                destination.Width,
                destination.Height);
        }

        VectorViewportTransform viewportTransform = VectorViewportTransform.Create(
            scene.Viewport,
            destination);
        if (!float.IsFinite(viewportTransform.Scale) || viewportTransform.Scale <= 0)
            return;
        Matrix3x2 originalTransform = callerTransform;
        Matrix3x2 sceneBaseTransform = hasPlacement
            ? placementFreeCallerTransform
            : callerTransform;

        int transformDepth = 0;
        int clipDepth = 0;
        int geometryIndex = 0;
        int textIndex = 0;
        int strokeIndex = 0;
        Exception? paintFailure = null;
        bool clipPopFailed = false;
        try
        {
            drawingSession.Transform = viewportTransform.Matrix * sceneBaseTransform;
            for (int i = 0; i < scene.Commands.Count; i++)
            {
                MarkdownVectorCommand command = scene.Commands[i];
                MarkdownVectorPaintStyle? style = command.Style;
                // Consume slots independently of draw visibility, opacity,
                // semantic overrides or the current switch branch. This is the
                // same immutable command order used by preparation, requiring
                // neither command-sized lookup maps nor paint allocations.
                CanvasGeometry? geometry = RequiresGeometry(command.Kind)
                    ? geometries[geometryIndex++] : null;
                CanvasTextLayout? textLayout = RequiresTextLayout(command)
                    ? textLayouts[textIndex++] : null;
                CanvasStrokeStyle? strokeStyle = style is not null && RequiresStrokeStyle(style)
                    ? strokeStyles[strokeIndex++] : null;
                switch (command.Kind)
                {
                    case MarkdownVectorCommandKind.BeginGroup:
                        transformStack[transformDepth++] = drawingSession.Transform;
                        drawingSession.Transform = ToMatrix(command.Transform) * drawingSession.Transform;
                        break;
                    case MarkdownVectorCommandKind.EndGroup:
                        if (transformDepth > 0)
                            drawingSession.Transform = transformStack[--transformDepth];
                        break;
                    case MarkdownVectorCommandKind.BeginClip:
                        if (geometry is { } clip)
                        {
                            CanvasActiveLayer layer;
                            try
                            {
                                layer = _testClipFactory is { } factory
                                    ? factory(drawingSession, clip)
                                    : drawingSession.CreateLayer(1f, clip);
                            }
                            catch
                            {
                                // A native factory can fail after an uncertain
                                // push. No returned wrapper can describe that
                                // depth, so never close known ancestors beneath
                                // it. Publish depth only after factory success.
                                clipPopFailed = true;
                                throw;
                            }
                            clipStack[clipDepth] = layer;
                            clipDepth++;
                        }
                        break;
                    case MarkdownVectorCommandKind.EndClip:
                        if (clipDepth > 0)
                        {
                            int activeClipIndex = clipDepth - 1;
                            CanvasActiveLayer? activeLayer = clipStack[activeClipIndex];
                            try
                            {
                                if (activeLayer is not null)
                                    NativeDisposer(activeLayer);
                            }
                            catch
                            {
                                // A failed pop leaves the native stack's exact
                                // depth unknown. Do not retry it, or pop an
                                // ancestor underneath it, during unwind.
                                clipPopFailed = true;
                                throw;
                            }
                            clipStack[activeClipIndex] = null;
                            clipDepth = activeClipIndex;
                        }
                        break;
                    case MarkdownVectorCommandKind.FillPath:
                        if (geometry is { } fillGeometry)
                            drawingSession.FillGeometry(fillGeometry, ResolveLegacyColor(command.ColorArgb, palette, highContrast, foregroundOverride));
                        break;
                    case MarkdownVectorCommandKind.StrokePath:
                        if (geometry is { } strokeGeometry)
                            drawingSession.DrawGeometry(strokeGeometry, ResolveLegacyColor(command.ColorArgb, palette, highContrast, foregroundOverride), command.StrokeWidth);
                        break;
                    case MarkdownVectorCommandKind.FillRectangle:
                        drawingSession.FillRectangle(ToRect(command.Rectangle), ResolveLegacyColor(command.ColorArgb, palette, highContrast, foregroundOverride));
                        break;
                    case MarkdownVectorCommandKind.StrokeRectangle:
                        drawingSession.DrawRectangle(ToRect(command.Rectangle), ResolveLegacyColor(command.ColorArgb, palette, highContrast, foregroundOverride), command.StrokeWidth);
                        break;
                    case MarkdownVectorCommandKind.StrokeLine:
                        drawingSession.DrawLine(command.Start.X, command.Start.Y, command.End.X, command.End.Y,
                            ResolveLegacyColor(command.ColorArgb, palette, highContrast, foregroundOverride), command.StrokeWidth);
                        break;
                    case MarkdownVectorCommandKind.DrawRectangle when style is not null:
                        DrawRectangle(scene, drawingSession, command, style, strokeStyle, palette, highContrast, foregroundOverride);
                        break;
                    case MarkdownVectorCommandKind.DrawPath when style is not null:
                        DrawPath(scene, drawingSession, command, geometry, style, strokeStyle, palette, highContrast, foregroundOverride);
                        break;
                    case MarkdownVectorCommandKind.DrawLine when style is not null:
                        if (style.StrokeWidth > 0 && TryResolveColor(style.StrokeArgb, style.Opacity,
                                palette, ResolvePaintRole(
                                    command,
                                    style.StrokeRole,
                                    style.StrokeArgb,
                                    style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground,
                                    highContrast),
                                highContrast, foregroundOverride, out Color lineColor))
                        {
                            drawingSession.DrawLine(command.Start.X, command.Start.Y, command.End.X, command.End.Y,
                                lineColor, style.StrokeWidth, strokeStyle);
                        }
                        break;
                    case MarkdownVectorCommandKind.DrawEllipse when style is not null:
                        DrawEllipse(scene, drawingSession, command, style, strokeStyle, palette, highContrast, foregroundOverride);
                        break;
                    case MarkdownVectorCommandKind.DrawText when style is not null:
                        DrawText(drawingSession, command, style, textLayout, palette, highContrast, foregroundOverride);
                        break;
                }
            }
        }
        catch (Exception exception)
        {
            paintFailure = exception;
        }

        Exception? clipCleanupFailure = null;
        if (clipPopFailed)
        {
            MarkClipCleanupFailed(drawingSession);
        }
        else
        {
            try
            {
                while (clipDepth > 0)
                {
                    int activeClipIndex = clipDepth - 1;
                    if (clipStack[activeClipIndex] is { } layer)
                        NativeDisposer(layer);
                    clipStack[activeClipIndex] = null;
                    clipDepth = activeClipIndex;
                }
            }
            catch (Exception exception)
            {
                // Unwind must obey the same LIFO rule as EndClip. The first
                // failed pop retains the remaining stack for quarantine.
                MarkClipCleanupFailed(drawingSession);
                clipCleanupFailure = exception;
            }
        }

        Exception? transformRestoreFailure = null;
        try
        {
            drawingSession.Transform = originalTransform;
        }
        catch (Exception exception)
        {
            transformRestoreFailure = exception;
        }

        if (paintFailure is not null)
        {
            if (clipCleanupFailure is not null && transformRestoreFailure is not null)
            {
                throw new AggregateException(
                    "Vector painting and cleanup both failed.",
                    paintFailure,
                    clipCleanupFailure,
                    transformRestoreFailure);
            }
            if (clipCleanupFailure is not null)
                throw new AggregateException("Vector painting and clip cleanup both failed.", paintFailure, clipCleanupFailure);
            if (transformRestoreFailure is not null)
                throw new AggregateException("Vector painting and transform restoration both failed.", paintFailure, transformRestoreFailure);

            ExceptionDispatchInfo.Capture(paintFailure).Throw();
        }

        if (clipCleanupFailure is not null)
        {
            if (transformRestoreFailure is not null)
            {
                throw new AggregateException(
                    "Vector clip cleanup and transform restoration both failed.",
                    clipCleanupFailure,
                    transformRestoreFailure);
            }

            ExceptionDispatchInfo.Capture(clipCleanupFailure).Throw();
        }

        if (transformRestoreFailure is not null)
            ExceptionDispatchInfo.Capture(transformRestoreFailure).Throw();
    }

    private void MarkClipCleanupFailed(CanvasDrawingSession drawingSession)
    {
        _resourcesPrepared = false;
        _resourceCleanupPending = true;
        _clipCleanupFailed = true;
        _linkedSemantics = null;
        _transformStack = null;
        // An uncertain inner native stack also forbids the outer boxes from
        // popping their session layers. This lookup neither creates state nor
        // allocates retirement owners on the already-failing path.
        NativePaintSessionResources.PoisonSession(drawingSession);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        try
        {
            ReleaseNativeResources();
        }
        finally
        {
            _scene = null;
            _resourceCreator = null;
            _nativeDisposer = null;
            _testClipFactory = null;
            _disposed = true;
            // Reversible release retains slots on the drawing. Terminal
            // retirement cannot rely on that drawing's survival: its outer
            // construction/snapshot/refill owner may drop it after this throws.
            // The source-free record/link was reserved before native creation.
            NativeResourceRetirementRecord? retirement = _nativeRetirement;
            _nativeRetirement = null;
            if (_resourceCleanupPending && retirement is not null)
            {
                retirement.SetGroup(0, _geometries, retryable: !_clipCleanupFailed);
                retirement.SetGroup(1, _textLayouts);
                retirement.SetGroup(2, _strokeStyles);
                retirement.SetGroup(3, _clipStack, retryable: false);
                retirement.SetTemporary(0, _pendingTextFormat);
                retirement.SetTemporary(1, _pendingPathBuilder);
                retirement.SetAdmissionCredit(_nativeCredit);
                _nativeCredit = null;
                _geometries = null;
                _textLayouts = null;
                _strokeStyles = null;
                _clipStack = null;
                _pendingTextFormat = null;
                _pendingPathBuilder = null;
                _resourceCleanupPending = false;
                retirement.Publish();
            }
            else
            {
                RefundNativeCredit();
            }
        }
    }

    private void RefundNativeCredit()
    {
        VectorNativeResourceCredit? credit = _nativeCredit;
        _nativeCredit = null;
        credit?.Dispose();
    }

    private Action<IDisposable> NativeDisposer => _nativeRetirement!.Disposer;

    private static void DisposeDeviceResources(
        ref CanvasGeometry?[]? geometries,
        ref CanvasTextLayout?[]? textLayouts,
        ref CanvasStrokeStyle?[]? strokeStyles,
        ref CanvasActiveLayer?[]? clipStack,
        ref CanvasTextFormat? pendingTextFormat,
        ref CanvasPathBuilder? pendingPathBuilder,
        Action<IDisposable> disposer,
        bool preserveGeometryForUncertainClips)
    {
        int count = Math.Max(geometries?.Length ?? 0,
            Math.Max(textLayouts?.Length ?? 0, strokeStyles?.Length ?? 0));
        BlockBoxCleanupScope cleanup = default;
        for (int i = 0; i < count; i++)
        {
            if (!preserveGeometryForUncertainClips && geometries is not null && i < geometries.Length)
                cleanup.TryDisposeAndClear(ref geometries[i], disposer);
            if (textLayouts is not null && i < textLayouts.Length)
                cleanup.TryDisposeAndClear(ref textLayouts[i], disposer);
            if (strokeStyles is not null && i < strokeStyles.Length)
                cleanup.TryDisposeAndClear(ref strokeStyles[i], disposer);
        }

        if (preserveGeometryForUncertainClips || !AreAllResourcesCleared(clipStack))
            cleanup.RecordFailure(new InvalidOperationException(
                "An uncertain vector active layer and its clip geometry require terminal quarantine, not post-session disposal."));
        cleanup.TryDisposeAndClear(ref pendingTextFormat, disposer);
        cleanup.TryDisposeAndClear(ref pendingPathBuilder, disposer);

        if (AreAllResourcesCleared(geometries))
            geometries = null;
        if (AreAllResourcesCleared(textLayouts))
            textLayouts = null;
        if (AreAllResourcesCleared(strokeStyles))
            strokeStyles = null;
        if (AreAllResourcesCleared(clipStack))
            clipStack = null;

        cleanup.ThrowIfAny("disposing vector drawing resources");
    }

    private static bool AreAllResourcesCleared<T>(T?[]? resources)
        where T : class, IDisposable
    {
        if (resources is null)
            return true;

        for (int i = 0; i < resources.Length; i++)
        {
            if (resources[i] is not null)
                return false;
        }

        return true;
    }

    private static T?[] CreateSlots<T>(int count) where T : class
        => count == 0 ? Array.Empty<T?>() : new T?[count];

    private static bool RequiresGeometry(MarkdownVectorCommandKind kind)
        => kind is MarkdownVectorCommandKind.FillPath or
            MarkdownVectorCommandKind.StrokePath or
            MarkdownVectorCommandKind.DrawPath or
            MarkdownVectorCommandKind.BeginClip;

    private static bool RequiresTextLayout(MarkdownVectorCommand command)
        => command.Kind == MarkdownVectorCommandKind.DrawText &&
            command.Text is not null && command.TextStyle is not null;

    internal static long EstimateNativeResourceWeightUnits(
        MarkdownVectorScene scene,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return CountNativeResourceShape(scene, cancellationToken, calculateWeight: true).WeightUnits;
    }

    private static NativeResourceShape CountNativeResourceShape(
        MarkdownVectorScene scene,
        CancellationToken cancellationToken,
        bool calculateWeight)
    {
        int geometries = 0;
        int textLayouts = 0;
        int strokeStyles = 0;
        int transformDepth = 0;
        int clipDepth = 0;
        int maximumTransformDepth = 0;
        int maximumClipDepth = 0;
        long weightUnits = calculateWeight ? 1024 : 0; // Per-scene fixed custody and paint overhead.
        if (calculateWeight)
        {
            weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(scene.Commands.Count, 128));
            weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(scene.Semantics.Count, 256));
            weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(scene.Links.Count, 256));
        }

        if (calculateWeight)
        {
            foreach (MarkdownVectorSemanticItem semantic in scene.Semantics)
            {
                cancellationToken.ThrowIfCancellationRequested();
                weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(semantic.Name?.Length ?? 0, 16));
                weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(semantic.Description?.Length ?? 0, 16));
            }
            foreach (MarkdownVectorLinkAction link in scene.Links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(link.Target?.Length ?? 0, 16));
                weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(link.Action?.Length ?? 0, 16));
            }
        }

        foreach (MarkdownVectorCommand command in scene.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RequiresGeometry(command.Kind))
            {
                geometries++;
                if (calculateWeight)
                {
                    weightUnits = SaturatingAdd(weightUnits, 2048);
                    weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(command.Path.Count, 192));
                    // Cubics and quadratics carry more control-point work than a
                    // straight segment; weight them without inspecting coordinates.
                    for (int operationIndex = 0; operationIndex < command.Path.Count; operationIndex++)
                    {
                        if ((operationIndex & 0xFF) == 0)
                            cancellationToken.ThrowIfCancellationRequested();
                        MarkdownVectorPathOperationKind kind = command.Path[operationIndex].Kind;
                        if (kind is MarkdownVectorPathOperationKind.QuadraticBezier)
                            weightUnits = SaturatingAdd(weightUnits, 128);
                        else if (kind is MarkdownVectorPathOperationKind.CubicBezier)
                            weightUnits = SaturatingAdd(weightUnits, 256);
                    }
                }
            }
            if (RequiresTextLayout(command))
            {
                textLayouts++;
                if (calculateWeight)
                {
                    weightUnits = SaturatingAdd(weightUnits, 4096);
                    weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(command.Text!.Length, 64));
                    weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(command.TextStyle!.FontFamily.Length, 16));
                }
            }
            if (command.Style is { } style && RequiresStrokeStyle(style))
            {
                strokeStyles++;
                if (calculateWeight)
                {
                    weightUnits = SaturatingAdd(weightUnits, 2048);
                    weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(style.DashArray.Count, 128));
                }
            }
            // MarkdownVectorScene validates complete, properly nested groups
            // and clips before publication. Group and clip depths are separate
            // because each uses a different paint stack, even when interleaved.
            switch (command.Kind)
            {
                case MarkdownVectorCommandKind.BeginGroup:
                    maximumTransformDepth = Math.Max(maximumTransformDepth, ++transformDepth);
                    break;
                case MarkdownVectorCommandKind.EndGroup:
                    transformDepth--;
                    break;
                case MarkdownVectorCommandKind.BeginClip:
                    maximumClipDepth = Math.Max(maximumClipDepth, ++clipDepth);
                    break;
                case MarkdownVectorCommandKind.EndClip:
                    clipDepth--;
                    break;
            }
        }
        if (calculateWeight)
        {
            weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(maximumTransformDepth, 512));
            if (textLayouts > 0)
                weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(maximumTransformDepth, 24));
            weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(maximumClipDepth, 4096));
        }
        return new NativeResourceShape(geometries, textLayouts, strokeStyles,
            maximumTransformDepth, maximumClipDepth, weightUnits);
    }

    private static long SaturatingMultiply(int count, int weight)
        => count <= 0 ? 0 : (long)count * weight;

    private static long SaturatingAdd(long left, long right)
        => right > long.MaxValue - left ? long.MaxValue : left + right;

    private readonly struct NativeResourceShape(
        int geometries, int textLayouts, int strokeStyles, int transformDepth, int clipDepth, long weightUnits)
    {
        internal int Geometries { get; } = geometries;
        internal int TextLayouts { get; } = textLayouts;
        internal int StrokeStyles { get; } = strokeStyles;
        internal int TransformDepth { get; } = transformDepth;
        internal int ClipDepth { get; } = clipDepth;
        internal long WeightUnits { get; } = weightUnits;
    }

    private void DrawRectangle(
        MarkdownVectorScene scene,
        CanvasDrawingSession ds,
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        CanvasStrokeStyle? strokeStyle,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        Rect rect = ToRect(command.Rectangle);
        MarkdownVectorPaintRole fillRole = ResolvePaintRole(
            command,
            style.FillRole,
            style.FillArgb,
            style.HighContrastFillRole ?? InferFillRole(command, style, GetSemanticRole(scene, command)),
            highContrast);
        if (TryResolveColor(style.FillArgb, style.Opacity, palette, fillRole,
                highContrast, foregroundOverride, out Color fill))
            ds.FillRectangle(rect, fill);
        if (style.StrokeWidth > 0 && TryResolveColor(style.StrokeArgb, style.Opacity, palette,
                ResolvePaintRole(
                    command,
                    style.StrokeRole,
                    style.StrokeArgb,
                    style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground,
                    highContrast),
                highContrast, foregroundOverride, out Color stroke))
            ds.DrawRectangle(rect, stroke, style.StrokeWidth, strokeStyle);
    }

    private void DrawPath(
        MarkdownVectorScene scene,
        CanvasDrawingSession ds,
        MarkdownVectorCommand command,
        CanvasGeometry? geometry,
        MarkdownVectorPaintStyle style,
        CanvasStrokeStyle? strokeStyle,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        if (geometry is null)
            return;
        MarkdownVectorPaintRole fillRole = ResolvePaintRole(
            command,
            style.FillRole,
            style.FillArgb,
            style.HighContrastFillRole ?? InferFillRole(command, style, GetSemanticRole(scene, command)),
            highContrast);
        if (TryResolveColor(style.FillArgb, style.Opacity, palette, fillRole,
                highContrast, foregroundOverride, out Color fill))
            ds.FillGeometry(geometry, fill);
        if (style.StrokeWidth > 0 && TryResolveColor(style.StrokeArgb, style.Opacity, palette,
                ResolvePaintRole(
                    command,
                    style.StrokeRole,
                    style.StrokeArgb,
                    style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground,
                    highContrast),
                highContrast, foregroundOverride, out Color stroke))
            ds.DrawGeometry(geometry, stroke, style.StrokeWidth, strokeStyle);
    }

    private void DrawEllipse(
        MarkdownVectorScene scene,
        CanvasDrawingSession ds,
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        CanvasStrokeStyle? strokeStyle,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        MarkdownVectorRectangle rectangle = command.Rectangle;
        float centerX = rectangle.X + rectangle.Width / 2f;
        float centerY = rectangle.Y + rectangle.Height / 2f;
        float radiusX = rectangle.Width / 2f;
        float radiusY = rectangle.Height / 2f;
        MarkdownVectorPaintRole fillRole = ResolvePaintRole(
            command,
            style.FillRole,
            style.FillArgb,
            style.HighContrastFillRole ?? InferFillRole(command, style, GetSemanticRole(scene, command)),
            highContrast);
        if (TryResolveColor(style.FillArgb, style.Opacity, palette, fillRole,
                highContrast, foregroundOverride, out Color fill))
            ds.FillEllipse(centerX, centerY, radiusX, radiusY, fill);
        if (style.StrokeWidth > 0 && TryResolveColor(style.StrokeArgb, style.Opacity, palette,
                ResolvePaintRole(
                    command,
                    style.StrokeRole,
                    style.StrokeArgb,
                    style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground,
                    highContrast),
                highContrast, foregroundOverride, out Color stroke))
            ds.DrawEllipse(centerX, centerY, radiusX, radiusY, stroke, style.StrokeWidth, strokeStyle);
    }

    private void DrawText(
        CanvasDrawingSession ds,
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        CanvasTextLayout? layout,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        uint? textArgb = style.FillArgb ?? style.StrokeArgb;
        MarkdownVectorPaintRole declaredRole = style.FillArgb is not null || style.StrokeArgb is null
            ? style.FillRole
            : style.StrokeRole;
        MarkdownVectorPaintRole highContrastRole = style.FillArgb is not null || style.StrokeArgb is null
            ? style.HighContrastFillRole ?? MarkdownVectorPaintRole.Foreground
            : style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground;
        if (layout is null || !TryResolveColor(textArgb, style.Opacity,
                palette, ResolvePaintRole(
                    command,
                    declaredRole,
                    textArgb,
                    highContrastRole,
                    highContrast),
                highContrast, foregroundOverride, out Color color))
        {
            return;
        }

        Vector2 origin = ResolveTextOrigin(command, layout);
        ds.DrawTextLayout(layout, origin.X, origin.Y, color);
    }

    private static Vector2 ResolveTextOrigin(
        MarkdownVectorCommand command,
        CanvasTextLayout layout)
    {
        float baseline = layout.LineMetrics.Length > 0
            ? layout.LineMetrics[0].Baseline
            : command.TextStyle!.FontSize * 0.8f;
        float width = (float)Math.Max(0, layout.DrawBounds.Width);
        float x = command.TextStyle!.Alignment switch
        {
            MarkdownVectorTextAlignment.Center => command.Start.X - width / 2f,
            MarkdownVectorTextAlignment.End => command.Start.X - width,
            _ => command.Start.X,
        };
        return new Vector2(x, command.Start.Y - baseline);
    }

    internal static CanvasStrokeStyle CreateStrokeStyle(MarkdownVectorPaintStyle style)
    {
        NativeResourceRetirementRecord retirement = NativeResourceRetirement.Shared.Reserve(
            resourceGroupCount: 0, temporarySlotCount: 1);
        CanvasStrokeStyle? result = new CanvasStrokeStyle();
        try
        {
            ConfigureStrokeStyle(result, style);
            return result;
        }
        catch (Exception preparationFailure)
        {
            BlockBoxCleanupScope cleanup = default;
            cleanup.TryDisposeAndClear(ref result, retirement.Disposer);
            retirement.SetTemporary(0, result);
            retirement.Publish();
            try { cleanup.ThrowIfAny("disposing unpublished vector stroke style"); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Vector stroke style preparation and cleanup both failed.",
                    preparationFailure, cleanupFailure);
            }
            throw;
        }
    }

    private static void ConfigureStrokeStyle(CanvasStrokeStyle result, MarkdownVectorPaintStyle style)
    {
        result.StartCap = ToCap(style.LineCap);
        result.EndCap = ToCap(style.LineCap);
        result.DashCap = ToCap(style.LineCap);
        result.LineJoin = ToJoin(style.LineJoin);
        result.DashOffset = style.DashOffset;
        result.TransformBehavior = style.NonScalingStroke
            ? CanvasStrokeTransformBehavior.Fixed
            : CanvasStrokeTransformBehavior.Normal;
        if (style.DashArray.Count > 0)
        {
            var dashes = new float[style.DashArray.Count];
            for (int i = 0; i < dashes.Length; i++)
                dashes[i] = style.DashArray[i];
            result.CustomDashStyle = dashes;
        }
    }

    internal static bool RequiresStrokeStyle(MarkdownVectorPaintStyle style)
        => style.StrokeWidth > 0 &&
           (style.NonScalingStroke ||
            style.DashArray.Count > 0 ||
            style.LineCap != MarkdownVectorLineCap.Flat ||
            style.LineJoin != MarkdownVectorLineJoin.Miter);

    private static CanvasCapStyle ToCap(MarkdownVectorLineCap value) => value switch
    {
        MarkdownVectorLineCap.Round => CanvasCapStyle.Round,
        MarkdownVectorLineCap.Square => CanvasCapStyle.Square,
        _ => CanvasCapStyle.Flat,
    };

    private static CanvasLineJoin ToJoin(MarkdownVectorLineJoin value) => value switch
    {
        MarkdownVectorLineJoin.Round => CanvasLineJoin.Round,
        MarkdownVectorLineJoin.Bevel => CanvasLineJoin.Bevel,
        _ => CanvasLineJoin.Miter,
    };

    private void CreateGeometry(
        ref CanvasGeometry? geometry,
        ICanvasResourceCreator resourceCreator,
        IReadOnlyList<MarkdownVectorPathOperation> operations,
        MarkdownVectorFillRule fillRule,
        CancellationToken cancellationToken)
    {
        CanvasPathBuilder builder = _pendingPathBuilder = new CanvasPathBuilder(resourceCreator);
        builder.SetFilledRegionDetermination(fillRule == MarkdownVectorFillRule.EvenOdd
            ? CanvasFilledRegionDetermination.Alternate
            : CanvasFilledRegionDetermination.Winding);
        bool figureOpen = false;
        for (int i = 0; i < operations.Count; i++)
        {
            if ((i & 0xff) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            MarkdownVectorPathOperation operation = operations[i];
            switch (operation.Kind)
            {
                case MarkdownVectorPathOperationKind.Move:
                    if (figureOpen)
                        builder.EndFigure(CanvasFigureLoop.Open);
                    builder.BeginFigure(ToVector2(operation.Point1));
                    figureOpen = true;
                    break;
                case MarkdownVectorPathOperationKind.Line when figureOpen:
                    builder.AddLine(ToVector2(operation.Point1));
                    break;
                case MarkdownVectorPathOperationKind.QuadraticBezier when figureOpen:
                    builder.AddQuadraticBezier(ToVector2(operation.Point1), ToVector2(operation.Point2));
                    break;
                case MarkdownVectorPathOperationKind.CubicBezier when figureOpen:
                    builder.AddCubicBezier(ToVector2(operation.Point1), ToVector2(operation.Point2), ToVector2(operation.Point3));
                    break;
                case MarkdownVectorPathOperationKind.Close when figureOpen:
                    builder.EndFigure(CanvasFigureLoop.Closed);
                    figureOpen = false;
                    break;
            }
        }
        if (figureOpen)
            builder.EndFigure(CanvasFigureLoop.Open);
        // A throwing builder cleanup must not prevent the returned geometry
        // from reaching an owned slot. The outer preparation cleanup attempts
        // both slots and retains any persistent failures.
        geometry = CanvasGeometry.CreatePath(builder);
        BlockBoxCleanupScope cleanup = default;
        cleanup.TryDisposeAndClear(ref _pendingPathBuilder, NativeDisposer);
        cleanup.ThrowIfAny("disposing vector path builder");
    }

    private static Matrix3x2 ToMatrix(MarkdownVectorTransform value) =>
        new(value.M11, value.M12, value.M21, value.M22, value.M31, value.M32);

    private static Vector2 ToVector2(MarkdownVectorPoint point) => new(point.X, point.Y);

    private static Rect ToRect(MarkdownVectorRectangle rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    private static Color ResolveLegacyColor(
        uint? argb,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        TryResolveColor(
            argb,
            1f,
            palette,
            highContrast ? MarkdownVectorPaintRole.Foreground : MarkdownVectorPaintRole.Authored,
            highContrast,
            foregroundOverride,
            out Color color);
        return color;
    }

    internal static Color ResolveColor(
        uint? argb,
        float opacity,
        VectorScenePaintPalette palette,
        MarkdownVectorPaintRole role,
        bool highContrast)
    {
        TryResolveColor(
            argb,
            opacity,
            palette,
            role,
            highContrast,
            foregroundOverride: null,
            out Color color);
        return color;
    }

    internal static string ResolveFontFamily(
        MarkdownVectorTextStyle style,
        string? hostFontFamily)
    {
        ArgumentNullException.ThrowIfNull(style);
        string selected = style.FontRole == MarkdownVectorFontRole.Host &&
               !string.IsNullOrWhiteSpace(hostFontFamily)
            ? hostFontFamily.Trim()
            : style.FontFamily;
        return SharedCanvasTextFormatCache.NormalizeFontFamilyForCanvas(selected);
    }

    internal static (float Width, float Height) ResolveTextLayoutSize(
        MarkdownVectorScene scene,
        string text,
        MarkdownVectorTextStyle style)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);

        // NoWrap keeps the authored line intact.  The estimate merely gives
        // DirectWrite enough finite measuring space; it does not scale or
        // otherwise alter the scene.  Use double arithmetic so adversarially
        // long labels cannot overflow before the clamp is applied.
        double estimatedTextWidth = checked((double)text.Length) * style.FontSize * 2d + 64d;
        double width = Math.Max(
            Math.Max(scene.Width, scene.Viewport.Width),
            estimatedTextWidth);
        double height = Math.Max(
            Math.Max(scene.Height, scene.Viewport.Height),
            style.FontSize * 4d + 64d);
        return (
            (float)Math.Clamp(width, 1d, MaximumTextLayoutDimension),
            (float)Math.Clamp(height, 1d, MaximumTextLayoutDimension));
    }

    private static bool TryResolveColor(
        uint? argb,
        float opacity,
        VectorScenePaintPalette palette,
        MarkdownVectorPaintRole role,
        bool highContrast,
        Color? foregroundOverride,
        out Color color)
    {
        byte authoredAlpha = argb is { } authored ? (byte)(authored >> 24) : (byte)0xFF;
        if (argb is not null && authoredAlpha == 0)
        {
            color = default;
            return false;
        }

        bool semanticMapping = argb is null || role != MarkdownVectorPaintRole.Authored;
        if (foregroundOverride is { } overrideColor)
        {
            color = Color.FromArgb(
                semanticMapping || argb is null ? overrideColor.A : authoredAlpha,
                overrideColor.R,
                overrideColor.G,
                overrideColor.B);
        }
        else if (semanticMapping)
        {
            color = palette.Resolve(role);
        }
        else
        {
            uint value = argb.GetValueOrDefault();
            byte alpha = (byte)(value >> 24);

            if (highContrast)
            {
                Color mapped = palette.Resolve(MarkdownVectorPaintRole.Foreground);
                // System High Contrast colors are semantic, opaque pairs. Retaining
                // authored alpha here can collapse thin strokes/text back into the
                // surface and violates the platform HC brush contract.
                color = Color.FromArgb(highContrast ? (byte)0xFF : alpha, mapped.R, mapped.G, mapped.B);
            }
            else
            {
                color = Color.FromArgb(alpha, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
            }
        }

        byte resolvedAlpha = highContrast && color.A > 0
            ? (byte)0xFF
            : (byte)Math.Clamp(
                (int)Math.Round(color.A * opacity * (semanticMapping ? authoredAlpha / 255f : 1f)),
                0,
                255);
        color = Color.FromArgb(resolvedAlpha, color.R, color.G, color.B);
        return color.A > 0;
    }

    private MarkdownVectorPaintRole ResolvePaintRole(
        MarkdownVectorCommand command,
        MarkdownVectorPaintRole declaredRole,
        uint? argb,
        MarkdownVectorPaintRole highContrastFallback,
        bool highContrast)
    {
        bool[]? linkedSemantics = _linkedSemantics;
        return ResolvePaintRole(
            command,
            declaredRole,
            argb,
            highContrastFallback,
            highContrast,
            linkedSemantics is not null &&
            command.SemanticIndex >= 0 &&
            command.SemanticIndex < linkedSemantics.Length &&
            linkedSemantics[command.SemanticIndex]);
    }

    internal static MarkdownVectorPaintRole ResolvePaintRole(
        MarkdownVectorCommand command,
        MarkdownVectorPaintRole declaredRole,
        uint? argb,
        MarkdownVectorPaintRole highContrastFallback,
        bool highContrast,
        bool linkedSemantic)
    {
        ArgumentNullException.ThrowIfNull(command);
        MarkdownVectorPaintRole role = declaredRole;
        if (role == MarkdownVectorPaintRole.Authored)
        {
            if (highContrast)
                role = highContrastFallback;
            else if (argb is null)
                role = MarkdownVectorPaintRole.Foreground;
            else
                return MarkdownVectorPaintRole.Authored;
        }

        return role == MarkdownVectorPaintRole.Foreground && linkedSemantic
            ? MarkdownVectorPaintRole.Link
            : role;
    }

    internal static MarkdownVectorPaintRole InferFillRole(
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        MarkdownVectorSemanticRole? semanticRole = null)
    {
        // Rectangle/ellipse fills are Mermaid surfaces even when they do not
        // carry a border (the full-scene background is a common example). A
        // filled path is treated as a surface only when its stroke identifies
        // it as a closed node; fill-only paths are commonly arrowheads or other
        // authored accents and should retain their light/dark palette.
        return ((command.Kind is MarkdownVectorCommandKind.DrawRectangle or
                    MarkdownVectorCommandKind.DrawEllipse) ||
                (command.Kind == MarkdownVectorCommandKind.DrawPath &&
                 (style.StrokeWidth > 0 ||
                  semanticRole is MarkdownVectorSemanticRole.Diagram or
                      MarkdownVectorSemanticRole.Group or
                      MarkdownVectorSemanticRole.Node or
                      MarkdownVectorSemanticRole.Legend)))
            ? MarkdownVectorPaintRole.Surface
            : MarkdownVectorPaintRole.Foreground;
    }

    private static MarkdownVectorSemanticRole? GetSemanticRole(
        MarkdownVectorScene scene,
        MarkdownVectorCommand command) =>
        command.SemanticIndex >= 0 && command.SemanticIndex < scene.Semantics.Count
            ? scene.Semantics[command.SemanticIndex].Role
            : null;

    private MarkdownVectorScene GetScene()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _scene ?? throw new ObjectDisposedException(nameof(VectorSceneDrawing));
    }
}

internal readonly struct VectorScenePaintPalette
{
    internal VectorScenePaintPalette(Color Foreground, Color Surface, Color Link)
    {
        this.Foreground = Foreground;
        this.Surface = Surface;
        this.Link = Link;
    }

    public Color Foreground { get; init; }
    public Color Surface { get; init; }
    public Color Link { get; init; }

    public Color Resolve(MarkdownVectorPaintRole role) => role switch
    {
        MarkdownVectorPaintRole.Surface => Surface,
        MarkdownVectorPaintRole.Link => Link,
        _ => Foreground,
    };
}
