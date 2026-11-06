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

    private readonly VectorScenePrimitivePainter _primitives;
    private readonly VectorSceneCommandPainter _commandPainter;
    private ICanvasResourceCreator? _resourceCreator;
    private NativeResourceRetirementRecord? _nativeRetirement;
    private readonly NativeResourceRetirement? _nativeRetirementOwner;
    private readonly MarkdownRenderResources _renderResources;
    private readonly VectorNativeResourceAdmission? _nativeAdmission;
    private VectorNativeResourceCredit? _nativeCredit;
    private volatile VectorNativePreparationStatus _nativePreparationStatus;
    private long _nativeResourceWeightUnits;
    private VectorNativePreparationOutcome _preparationOutcome;
    private Func<CanvasDrawingSession, CanvasGeometry, CanvasActiveLayer>? _clipLayerFactory;
    private MarkdownVectorScene? _scene;
    private readonly string? _hostFontFamily;
    private readonly MarkdownLayoutDiagnostics? _layoutDiagnostics;
    private CanvasGeometry?[]? _geometries;
    private CanvasTextLayout?[]? _textLayouts;
    private CanvasStrokeStyle?[]? _strokeStyles;
    private CanvasTextFormat? _pendingTextFormat;
    private CanvasPathBuilder? _pendingPathBuilder;
    private bool[]? _linkedSemantics;
    private Matrix3x2[]? _transformStack;
    private CanvasActiveLayer?[]? _clipStack;
    private VectorSceneNativeResourceShape? _nativeResourceShape;
    private volatile bool _resourcesPrepared;
    private volatile bool _resourceCleanupPending;
    private volatile bool _clipCleanupFailed;
    private volatile bool _disposed;

    internal VectorSceneDrawing(
        ICanvasResourceCreator resourceCreator,
        MarkdownVectorScene scene,
        CancellationToken cancellationToken,
        MarkdownRenderResources renderResources,
        string? hostFontFamily = null
        ,
        MarkdownLayoutDiagnostics? layoutDiagnostics = null
        ,
        bool deferNativeResources = false,
        NativeResourceRetirement? nativeRetirement = null,
        Func<CanvasDrawingSession, CanvasGeometry, CanvasActiveLayer>? clipLayerFactory = null,
        VectorNativeResourceAdmission? nativeAdmission = null)
    {
        _resourceCreator = resourceCreator ?? throw new ArgumentNullException(nameof(resourceCreator));
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _renderResources = renderResources ?? throw new ArgumentNullException(nameof(renderResources));
        _hostFontFamily = hostFontFamily;
        _primitives = new VectorScenePrimitivePainter(this);
        _commandPainter = new VectorSceneCommandPainter(this);
        _nativeRetirementOwner = nativeRetirement;
        _nativeAdmission = nativeAdmission;
        _clipLayerFactory = clipLayerFactory;
        _layoutDiagnostics = layoutDiagnostics;

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
            shape = VectorSceneResourceShapes.CountNativeResourceShape(scene, cancellationToken, calculateWeight: _nativeAdmission is not null);
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
        VectorSceneNativeResourceShape shape,
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
                _nativeRetirement = (_nativeRetirementOwner ?? _renderResources.NativeRetirement)
                    .Reserve();
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
        VectorSceneNativeResourceShape shape,
        CancellationToken cancellationToken)
    {
        // A rectangle/line command needs none of these native slots. Path and
        // text commands need just one kind. Do not retain five command-sized
        // scratch arrays per drawing, especially for large sparse diagrams.
        CanvasGeometry?[] geometries = VectorSceneNativeFactory.CreateSlots<CanvasGeometry>(shape.Geometries);
        CanvasTextLayout?[] textLayouts = VectorSceneNativeFactory.CreateSlots<CanvasTextLayout>(shape.TextLayouts);
        CanvasStrokeStyle?[] strokeStyles = VectorSceneNativeFactory.CreateSlots<CanvasStrokeStyle>(shape.StrokeStyles);
        var linkedSemantics = new bool[scene.Semantics.Count];
        Matrix3x2[] transformStack = shape.TransformDepth == 0
            ? Array.Empty<Matrix3x2>()
            : new Matrix3x2[shape.TransformDepth];
        CanvasActiveLayer?[] clipStack = VectorSceneNativeFactory.CreateSlots<CanvasActiveLayer>(shape.ClipDepth);
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
                if (VectorSceneResourceShapes.RequiresGeometry(command.Kind))
                {
                    CreateGeometry(
                        ref geometries[geometryIndex++],
                        _resourceCreator!,
                        command.Path,
                        command.Style?.FillRule ?? MarkdownVectorFillRule.Nonzero,
                        cancellationToken);
                }

                if (VectorSceneResourceShapes.RequiresTextLayout(command))
                    CreateTextLayout(scene, command, i, textLayouts, textIndex++);

                if (command.Style is { } style && RequiresStrokeStyle(style))
                {
                    // Publish the native owner before property setters or a
                    // custom dash-array copy can fail.
                    int strokeSlot = strokeIndex++;
                    strokeStyles[strokeSlot] = new CanvasStrokeStyle();
                    VectorSceneNativeFactory.ConfigureStrokeStyle(strokeStyles[strokeSlot]!, style);
                }
            }
        }
        catch (Exception preparationException)
        {
            PublishFailedPreparation(geometries, textLayouts, strokeStyles, clipStack, preparationException);
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

    // Creates one command's text layout in its slot through a temporary
    // format that is always disposed; format cleanup never hides a layout failure.
    private void CreateTextLayout(
        MarkdownVectorScene scene,
        MarkdownVectorCommand command,
        int commandIndex,
        CanvasTextLayout?[] textLayouts,
        int textSlot)
    {
        string text = command.Text!;
        MarkdownVectorTextStyle textStyle = command.TextStyle!;
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
                textLayouts[textSlot] = CanvasTextLayoutFactory.Create(
                    _resourceCreator!,
                    text,
                    format,
                    layoutWidth,
                    layoutHeight,
                    _layoutDiagnostics);
            }
            catch (Exception exception) when (
                exception is ArgumentException or COMException)
            {
                throw new ArgumentException(
                    $"Win2D rejected vector text command {commandIndex} " +
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

    // Publishes a failed preparation's partial slots to this drawing before
    // releasing them, so failed native cleanup keeps a retry owner. The caller
    // rethrows the preparation failure.
    private void PublishFailedPreparation(
        CanvasGeometry?[] geometries,
        CanvasTextLayout?[] textLayouts,
        CanvasStrokeStyle?[] strokeStyles,
        CanvasActiveLayer?[] clipStack,
        Exception preparationException)
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
            VectorSceneNativeFactory.DisposeDeviceResources(
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
        LayoutPlacementTransformContext placement,
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

        _commandPainter.Paint(
            placement,
            drawingSession,
            scene,
            new VectorScenePaintSlots(geometries, textLayouts, strokeStyles, transformStack, clipStack),
            destination,
            palette,
            highContrast,
            foregroundOverride);
    }

    internal void MarkClipCleanupFailed(CanvasDrawingSession drawingSession)
    {
        _resourcesPrepared = false;
        _resourceCleanupPending = true;
        _clipCleanupFailed = true;
        _linkedSemantics = null;
        _transformStack = null;
        // An uncertain inner native stack also forbids the outer boxes from
        // popping their session layers. This lookup neither creates state nor
        // allocates retirement owners on the already-failing path.
        _renderResources.PaintSessions.PoisonSession(drawingSession);
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
            _clipLayerFactory = null;
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

    internal Action<IDisposable> NativeDisposer => _nativeRetirement!.Disposer;

    internal VectorScenePrimitivePainter Primitives => _primitives;

    /// <summary>
    /// The clip-layer factory BeginClip uses instead of
    /// <see cref="CanvasDrawingSession.CreateLayer(float, CanvasGeometry)"/>, if
    /// the drawing was created with one. Native-lifetime tests use it to fail a
    /// push at a known depth.
    /// </summary>
    internal Func<CanvasDrawingSession, CanvasGeometry, CanvasActiveLayer>? ClipLayerFactory => _clipLayerFactory;

    internal static long EstimateNativeResourceWeightUnits(
        MarkdownVectorScene scene,
        CancellationToken cancellationToken)
        => VectorSceneResourceShapes.EstimateNativeResourceWeightUnits(scene, cancellationToken);

    internal static bool RequiresStrokeStyle(MarkdownVectorPaintStyle style)
        => VectorSceneResourceShapes.RequiresStrokeStyle(style);

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
                    builder.BeginFigure(VectorSceneNativeFactory.ToVector2(operation.Point1));
                    figureOpen = true;
                    break;
                case MarkdownVectorPathOperationKind.Line when figureOpen:
                    builder.AddLine(VectorSceneNativeFactory.ToVector2(operation.Point1));
                    break;
                case MarkdownVectorPathOperationKind.QuadraticBezier when figureOpen:
                    builder.AddQuadraticBezier(VectorSceneNativeFactory.ToVector2(operation.Point1), VectorSceneNativeFactory.ToVector2(operation.Point2));
                    break;
                case MarkdownVectorPathOperationKind.CubicBezier when figureOpen:
                    builder.AddCubicBezier(VectorSceneNativeFactory.ToVector2(operation.Point1), VectorSceneNativeFactory.ToVector2(operation.Point2), VectorSceneNativeFactory.ToVector2(operation.Point3));
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

    internal static Color ResolveColor(
        uint? argb,
        float opacity,
        VectorScenePaintPalette palette,
        MarkdownVectorPaintRole role,
        bool highContrast)
        => VectorScenePaintResolver.ResolveColor(argb, opacity, palette, role, highContrast);

    internal static string ResolveFontFamily(
        MarkdownVectorTextStyle style,
        string? hostFontFamily)
        => VectorScenePaintResolver.ResolveFontFamily(style, hostFontFamily);

    internal static (float Width, float Height) ResolveTextLayoutSize(
        MarkdownVectorScene scene,
        string text,
        MarkdownVectorTextStyle style)
        => VectorScenePaintResolver.ResolveTextLayoutSize(scene, text, style);

    internal MarkdownVectorPaintRole ResolvePaintRole(
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
        => VectorScenePaintResolver.InferFillRole(command, style, semanticRole);

    private MarkdownVectorScene GetScene()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _scene ?? throw new ObjectDisposedException(nameof(VectorSceneDrawing));
    }
}
