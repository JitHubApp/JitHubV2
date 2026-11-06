using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// A block-level painter-neutral scene with an intrinsic viewport. It owns only
/// local horizontal overflow; the document remains the sole vertical scroller.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because the box is the one owner that
/// sequences its drawing swap, capacity fallback and horizontal-overflow
/// viewport through Measure, Paint, release and terminal Dispose, and it
/// implements the overflow and selection-geometry interfaces those steps feed.
/// Hit testing, selection and fallback painting, semantic styling and
/// detached-fallback custody already live in their own collaborators.
/// </remarks>
internal sealed class VectorSceneBox : BlockBox, IHorizontalOverflowBox, ISelectionBackgroundGeometrySource
{

    internal readonly MarkdownLayoutContext LayoutContext;
    private readonly VectorSceneSelectionPainter _selection;
    private readonly VectorSceneFallbackPainter _fallback;
    private readonly VectorSceneSemanticHitTester _hitTester;
    private VectorSceneDrawing _drawing;
    private readonly VectorSceneDetachedFallback _detachedFallback;
    private readonly MarkdownVectorRectangle _semanticViewport;
    internal readonly IReadOnlyList<MarkdownVectorSemanticItem> SemanticItems;
    private readonly IReadOnlyList<MarkdownVectorLinkAction?> _semanticLinks;
    private readonly ReadOnlyMemory<int> _semanticDepths;
    internal readonly SourceSpan SourceRange;
    internal readonly MarkdownAccessibilityRole AccessibilityRole;
    internal readonly string? AccessibilityName;
    internal readonly string? AccessibilityDescription;
    internal readonly string? SemanticText;
    internal readonly string ElementKey;
    private readonly float _sceneWidth;
    private readonly float _sceneHeight;
    private readonly float _referenceFontSize;
    private readonly string _capacityFallbackText;
    private readonly string _capacityFallbackFailureText;
    // Projection metadata is O(semantic count), replacing two retained int
    // command-index entries with one object reference per semantic (8 vs 8
    // bytes on x64; 4 vs 8 on x86). Each indexed text semantic also retains
    // one frozen ElementStyle object; non-text semantics share the container
    // style. No command, scene, or native object is retained.
    private readonly ElementStyle[] _semanticTextStyles;
    private readonly IReadOnlyList<string> _styleContextKeys;
    private readonly IReadOnlyList<string> _styleAliasKeys;
    private ElementStyle? _style;
    private float _viewportWidth;
    private float _contentWidth;
    private float _contentHeight;
    private double _horizontalOffset;
    private Rect _contentBounds;
    private bool _initialMeasurePreparedResources;
    private bool _disposed;
    private volatile bool _graphRebindFailed;

    internal VectorSceneBox(
        MarkdownLayoutContext context,
        MarkdownContent content,
        string elementKey,
        VectorNativeResourceAdmission? nativeAdmission = null)
    {
        LayoutContext = context ?? throw new ArgumentNullException(nameof(context));
        _hitTester = new VectorSceneSemanticHitTester(this);
        _fallback = new VectorSceneFallbackPainter(this);
        _selection = new VectorSceneSelectionPainter(this);
        _detachedFallback = new VectorSceneDetachedFallback(this);
        ArgumentNullException.ThrowIfNull(content);
        MarkdownVectorScene scene = content.VectorScene ?? throw new ArgumentException("Vector content requires a scene.", nameof(content));
        _semanticViewport = scene.Viewport;
        SemanticItems = scene.Semantics;
        _semanticLinks = scene.SemanticLinks;
        _semanticDepths = scene.SemanticDepths;
        SourceRange = content.SourceSpan;
        AccessibilityRole = content.AccessibilityRole;
        AccessibilityName = content.AccessibilityName;
        AccessibilityDescription = content.AccessibilityDescription;
        SemanticText = content.SemanticText;
        VectorNativeResourceAdmission? resolvedNativeAdmission =
            VectorSceneCapacityFallback.ResolveNativeAdmission(context, nativeAdmission);
        _capacityFallbackText = resolvedNativeAdmission is null
            ? string.Empty
            : VectorSceneCapacityFallback.CreateBlockText(
                context,
                SourceRange,
                SemanticText,
                AccessibilityName,
                failed: false);
        _capacityFallbackFailureText = resolvedNativeAdmission is null
            ? string.Empty
            : VectorSceneCapacityFallback.CreateBlockText(
                context,
                SourceRange,
                SemanticText,
                AccessibilityName,
                failed: true);
        ElementKey = string.IsNullOrWhiteSpace(elementKey) ? MarkdownElementKeys.Diagram : elementKey;
        _sceneWidth = scene.Width;
        _sceneHeight = scene.Height;
        (int[] textCommandIndices, int[] backgroundCommandIndices, float referenceFontSize) =
            VectorSceneSemanticCommandIndex.IndexSemanticCommands(scene, context.CancellationToken);
        _referenceFontSize = referenceFontSize;
        _styleContextKeys = context.CreateStyleContextSnapshot();
        _styleAliasKeys = context.CreateStyleAliasSnapshot();
        _drawing = new VectorSceneDrawing(
            context.ResourceCreator,
            scene,
            context.CancellationToken,
            context.RenderResources,
            Style.FontFamily
            , context.LayoutDiagnostics
            , deferNativeResources: true,
            nativeAdmission: resolvedNativeAdmission);
        Margin = Style.Margin;
        _semanticTextStyles = new VectorSceneSemanticStyleBuilder(this).BuildSemanticTextStyles(
            scene,
            textCommandIndices,
            backgroundCommandIndices);
    }

    // Drawing is the sole scene owner. Terminal retirement clears its scene
    // reference; immutable projections retain exact semantic/accessibility
    // metadata without retaining the command graph.
    internal MarkdownVectorScene Scene => _drawing.Scene;

    // Borrowed by optional preparation code. The context owns no scene command
    // graph, and callers capture drawing inputs under snapshot exclusion before
    // doing replacement work off-lock.
    internal bool IsGraphDetached => !_disposed && !_drawing.HasScene;

    internal bool GraphRebindFailed => _graphRebindFailed;

    internal IReadOnlyList<MarkdownVectorLinkAction?> SemanticLinks => _semanticLinks;

    internal ReadOnlySpan<int> SemanticDepths => _semanticDepths.Span;

    internal MarkdownVectorRectangle SemanticViewport => _semanticViewport;

    /// <summary>The capacity-fallback text for the current rebind outcome.</summary>
    internal string CapacityFallbackText =>
        _graphRebindFailed ? _capacityFallbackFailureText : _capacityFallbackText;

    internal CanvasTextLayout? DetachedFallbackLayout => _detachedFallback.Layout;

    internal VectorSceneDrawing Drawing => _drawing;

    internal VectorSceneFallbackPainter FallbackPainter => _fallback;

    internal MarkdownDeferredSceneBinding? DeferredSceneBinding { get; init; }

    internal MarkdownVectorLinkAction? GetSemanticLinkAction(int semanticIndex) =>
        (uint)semanticIndex < (uint)_semanticLinks.Count ? _semanticLinks[semanticIndex] : null;

    internal Rect ContentBounds => _contentBounds;

    internal Rect VisibleContentBounds =>
        VectorViewportTransform.Intersect(_contentBounds, HorizontalViewportBounds);

    internal bool IsNativeResourcesPrepared => _drawing.IsPrepared;

    /// <summary>Admission ledger used by this scene, or null for legacy eager rendering.</summary>
    internal VectorNativeResourceAdmission? NativeAdmission => _drawing.NativeResourceAdmission;

    /// <summary>Most recent bounded-admission result for native scene resources.</summary>
    internal VectorNativePreparationStatus NativePreparationStatus => _drawing.NativePreparationStatus;

    /// <summary>
    /// True only when a capacity/size denial has a prewarmed, bounded text
    /// representation. This is independent of native-scene readiness.
    /// </summary>
    internal bool IsCapacityFallbackPaintable =>
        IsCapacityPreparationStatus(NativePreparationStatus) && HasCapacityFallbackText &&
        VectorSceneCapacityFallback.TryGetFormat(NativeAdmission, LayoutContext.FlowDirection, out _);

    /// <summary>
    /// Recreates this scene's device resources before paint. Callers must do
    /// this from the background layout/preparation path, never from Paint.
    /// </summary>
    internal void PrepareNativeResources(CancellationToken cancellationToken)
        => _ = TryPrepareNativeResources(cancellationToken, out _);

    internal bool TryPrepareNativeResources(
        CancellationToken cancellationToken,
        out VectorNativePreparationOutcome outcome)
    {
        bool prepared = _drawing.TryPrepareNativeResources(cancellationToken, out outcome);
        if (outcome.Status is VectorNativePreparationStatus.DeferredCapacity or VectorNativePreparationStatus.TooLarge)
            _ = PrepareCapacityFallback(cancellationToken);
        return prepared;
    }

    /// <summary>Warms the admission-owned fallback descriptor off the paint path.</summary>
    internal bool PrepareCapacityFallback(CancellationToken cancellationToken)
        => PrepareCapacityFallback(cancellationToken, _drawing.NativeResourceAdmission);

    private bool PrepareCapacityFallback(
        CancellationToken cancellationToken,
        VectorNativeResourceAdmission? nativeAdmission)
    {
        if (_disposed || nativeAdmission is null || !HasCapacityFallbackText)
            return false;
        cancellationToken.ThrowIfCancellationRequested();
        // Capacity denial is the only typed non-exceptional fallback result.
        // A DirectWrite/Win2D descriptor creation failure must escape through
        // the existing device/native-resource recovery path; swallowing it
        // would publish an unpaintable scene as a settled capacity fallback.
        return nativeAdmission.CapacityFallbackFormats.PrepareFormat(
            LayoutContext.RenderResources.TextFormats,
            LayoutContext.FlowDirection);
    }

    /// <summary>Releases only device resources; the immutable scene stays owned by this box.</summary>
    internal void ReleaseNativeResources()
    {
        Exception? drawingFailure = null;
        try
        {
            _drawing.ReleaseNativeResources();
        }
        catch (Exception exception)
        {
            drawingFailure = exception;
        }
        try
        {
            _detachedFallback.Release();
        }
        catch (Exception fallbackFailure) when (drawingFailure is not null)
        {
            throw new AggregateException(
                "Vector drawing and detached fallback cleanup both failed.",
                drawingFailure, fallbackFailure);
        }
        if (drawingFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(drawingFailure).Throw();
    }

    /// <summary>Releases device resources and detaches the immutable command graph.</summary>
    internal void ReleaseSceneGraph()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _drawing.DetachSceneGraph();
        _detachedFallback.Release();
    }

    internal bool IsDisposed => _disposed;

    internal bool HasDetachedFallback =>
        _detachedFallback.HasPaintableLayout ||
        (NativeAdmission is not null && IsGraphDetached && HasCapacityFallbackText &&
            VectorSceneCapacityFallback.TryGetFormat(NativeAdmission, LayoutContext.FlowDirection, out _));

    /// <summary>Transfers a staged layout; the caller disposes any rejected candidate.</summary>
    internal bool TryAdoptDetachedFallback(
        ref CanvasTextLayout? candidate,
        CancellationToken cancellationToken)
        => _detachedFallback.TryAdopt(ref candidate, cancellationToken);

    internal bool TryBeginDetachedFallbackCandidate(NativeResourceRetirementRecord retirementRecord)
        => _detachedFallback.TryBeginCandidate(retirementRecord);

    internal NativeResourceRetirementRecord? GetDetachedFallbackCandidateRecord() =>
        _detachedFallback.CandidateRecord;

    internal void EndDetachedFallbackCandidate(NativeResourceRetirementRecord retirementRecord) =>
        _detachedFallback.EndCandidate(retirementRecord);

    /// <summary>Marks a failed visible-band graph rebind and refreshes its fallback status.</summary>
    internal void MarkGraphRebindFailed()
    {
        if (_disposed)
            return;

        _graphRebindFailed = true;
    }

    /// <summary>Atomically adopts a prepared replacement for a detached scene.</summary>
    internal void AdoptPreparedReplacement(VectorSceneDrawing replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsGraphDetached)
            throw new InvalidOperationException("A scene replacement can only be adopted by a detached vector box.");
        bool replacementPrepared = replacement.IsPrepared;
        bool replacementCapacityFallbackReady =
            (replacement.NativePreparationStatus is VectorNativePreparationStatus.DeferredCapacity or
                VectorNativePreparationStatus.TooLarge) &&
            replacement.NativeResourceAdmission is not null &&
            HasCapacityFallbackText &&
            VectorSceneCapacityFallback.TryGetFormat(
                replacement.NativeResourceAdmission, LayoutContext.FlowDirection, out _);
        if (ReferenceEquals(replacement, _drawing) || !replacement.HasScene ||
            (!replacementPrepared && !replacementCapacityFallbackReady))
        {
            throw new InvalidOperationException(
                "A replacement drawing must own a prepared scene or a prewarmed bounded-capacity fallback.");
        }

        _detachedFallback.Release();
        VectorSceneDrawing previous = _drawing;
        // The previous drawing is already detached and has no device resources.
        // Dispose it before the field swap so any unexpected cleanup failure
        // leaves the caller owning the prepared replacement.
        previous.Dispose();
        _drawing = replacement;
        _graphRebindFailed = false;
    }

    internal ElementStyle ResolvedStyle => Style;

    public double HorizontalOffset => _horizontalOffset;

    public double HorizontalExtent => Math.Max(_contentWidth, _viewportWidth);

    public double HorizontalViewport => _viewportWidth;

    public bool CanScrollHorizontally => HorizontalExtent > HorizontalViewport + 0.5;

    public bool IsRightToLeft => LayoutContext.FlowDirection == Microsoft.UI.Xaml.FlowDirection.RightToLeft;

    public Rect HorizontalViewportBounds => new(
        Bounds.X + Margin.Left + Style.Padding.Left,
        Bounds.Y + Margin.Top + Style.Padding.Top,
        Math.Max(0, _viewportWidth),
        Math.Max(0, _contentHeight));

    public Rect HorizontalScrollTrackBounds => HorizontalOverflowScrollbar.GetTrackBelowViewport(this);

    public Rect HorizontalScrollThumbBounds => HorizontalOverflowScrollbar.GetThumbBounds(this);

    public override float Measure(float availableWidth)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfCancellationRequested();
        Margin = Style.Margin;
        _viewportWidth = (float)Math.Max(
            1,
            availableWidth - Margin.Left - Margin.Right - Style.Padding.Left - Style.Padding.Right);
        float scale = ResolveContentScale(
            _referenceFontSize,
            Style.FontSize,
            LayoutContext.ThemeSnapshot.TextScaleFactor);
        _contentWidth = Math.Max(1, _sceneWidth * scale);
        _contentHeight = Math.Max(1, _sceneHeight * scale);
        _horizontalOffset = Math.Clamp(_horizontalOffset, 0, Math.Max(0, HorizontalExtent - HorizontalViewport));
        double height = Margin.Top + Style.Padding.Top + _contentHeight + Style.Padding.Bottom + Margin.Bottom;
        if (CanScrollHorizontally)
            height += HorizontalOverflowScrollbar.Height;
        Bounds = new Rect(0, 0, availableWidth, height);
        UpdateContentBounds();
        // Block scenes are prepared during layout, not in the per-frame paint
        // callback. In a lazy snapshot this work is limited to the measured
        // viewport band; off-band resources can later be retired safely.
        if (_drawing.HasScene &&
            !LayoutContext.UseProgressiveNativeSceneResidency &&
            !_initialMeasurePreparedResources)
        {
            if (_drawing.NativeResourceAdmission is null)
                _drawing.PrepareNativeResources(LayoutContext.CancellationToken);
            else
                _ = TryPrepareNativeResources(LayoutContext.CancellationToken, out _);
            _initialMeasurePreparedResources = true;
        }
        return (float)height;
    }

    public override void Arrange(float x, float y, float width)
    {
        Bounds = new Rect(x, y, width, Bounds.Height);
        UpdateContentBounds();
        IsDirty = false;
    }

    internal override void ThrowIfCancellationRequested() =>
        LayoutContext.CancellationToken.ThrowIfCancellationRequested();

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool paintScene = _drawing.HasScene && _drawing.IsPrepared;
        bool paintFallback = IsGraphDetached && HasDetachedFallback;
        bool paintCapacityFallback = !IsGraphDetached && IsCapacityFallbackPaintable;
        if (!paintScene && !paintFallback && !paintCapacityFallback)
            return;

        Rect outer = OuterBounds;
        if (outer.Width <= 0 || outer.Height <= 0)
            return;
        float radius = Math.Max(0, Style.CornerRadius);
        if (Style.Background is { } background)
            ds.FillRoundedRectangle(outer, radius, radius, background);

        if (paintScene)
            PaintScene(ds);
        else if (paintFallback)
            _fallback.PaintDetachedFallback(ds, Style.Foreground);
        else
            _fallback.PaintCapacityFallback(ds, Style.Foreground);

        if (Style.BorderBrush is { } border && Style.BorderThickness > 0)
        {
            float inset = Style.BorderThickness / 2f;
            ds.DrawRoundedRectangle(
                new Rect(outer.X + inset, outer.Y + inset,
                    Math.Max(0, outer.Width - Style.BorderThickness),
                    Math.Max(0, outer.Height - Style.BorderThickness)),
                radius,
                radius,
                border,
                Style.BorderThickness);
        }
        PaintHorizontalScrollbar(ds);
    }

    public override bool HitTest(Point point, out DocumentPosition position)
        => _hitTester.HitTest(point, out position);

    internal Rect GetSemanticBounds(int semanticIndex)
        => _hitTester.GetSemanticBounds(semanticIndex);

    internal Rect GetVisibleSemanticBounds(int semanticIndex) =>
        VectorViewportTransform.Intersect(
            GetSemanticBounds(semanticIndex),
            HorizontalViewportBounds);

    internal bool TryGetTextPositionAt(Point point, out DocumentPosition position)
        => _hitTester.TryGetTextPositionAt(point, out position);

    internal override bool HitTestSelectionEndpoint(Point point, out DocumentPosition position)
        => _selection.HitTestSelectionEndpoint(point, out position);

    public override IEnumerable<Rect> GetSelectionRects(DocumentRange range)
        => _selection.GetSelectionRects(range);

    /// <summary>
    /// Returns paint-only selection geometry. The full block bounds remain
    /// authoritative for selection endpoints and point-in-selection tests;
    /// the adorner background follows only visible scene content.
    /// </summary>
    public IEnumerable<SelectionBackgroundGeometry> GetSelectionBackgroundGeometry(
        DocumentRange range)
        => _selection.GetSelectionBackgroundGeometry(range);

    public override void PaintSelectionForeground(
        CanvasDrawingSession ds,
        DocumentRange range,
        Color color,
        Rect viewport)
        => _selection.PaintSelectionForeground(ds, range, color, viewport);

    internal bool TryGetLinkAt(Point point, out MarkdownVectorLinkAction? link)
        => _hitTester.TryGetLinkAt(point, out link);

    internal bool TryGetLink(int semanticIndex, out MarkdownVectorLinkAction? link)
        => _hitTester.TryGetLink(semanticIndex, out link);

    internal MarkdownVectorSemanticItem? GetSemanticItem(int semanticIndex) =>
        (uint)semanticIndex < (uint)SemanticItems.Count ? SemanticItems[semanticIndex] : null;

    internal CancellationToken CancellationToken => LayoutContext.CancellationToken;

    /// <summary>Returns the frozen draw-time typography and semantic paint projection.</summary>
    internal ElementStyle ResolveSemanticTextStyle(int semanticIndex) =>
        (uint)semanticIndex < (uint)_semanticTextStyles.Length
            ? _semanticTextStyles[semanticIndex]
            : Style;

    internal bool IsSelectableAt(Point point)
        => _hitTester.IsSelectableAt(point);

    internal bool ContainsSceneViewport(Point point) => HorizontalViewportBounds.Contains(point);

    public bool ScrollHorizontal(double delta) => SetHorizontalOffset(HorizontalOffset + delta);

    public bool SetHorizontalOffset(double offset)
    {
        double next = Math.Clamp(offset, 0, Math.Max(0, HorizontalExtent - HorizontalViewport));
        if (Math.Abs(next - _horizontalOffset) <= 0.1)
            return false;
        _horizontalOffset = next;
        UpdateContentBounds();
        return true;
    }

    public override void Dispose()
    {
        // LayoutSnapshot serializes terminal disposal with paint/interaction
        // readers. A retained retired box must not pin provider command trees;
        // scalar and semantic projections intentionally survive for UIA reads.
        BlockBoxCleanupScope cleanup = default;
        cleanup.TryDispose(_drawing);
        // Sets _disposed after the fallback slot leaves its owner and before
        // the fallback's retirement record is published.
        _detachedFallback.RetireWithHost(ref cleanup, ref _disposed);
        cleanup.ThrowIfAny("disposing vector scene box");
    }

    private static float ResolveContentScale(
        float reference,
        float resolvedStyleFontSize,
        double textScaleFactor)
    {
        double scale = reference > 0 && float.IsFinite(resolvedStyleFontSize) && resolvedStyleFontSize > 0
            ? resolvedStyleFontSize / reference
            : textScaleFactor;
        return (float)Math.Clamp(double.IsFinite(scale) ? scale : 1d, 0.1d, 10d);
    }

    internal ElementStyle Style => _style ??= LayoutContext.ThemeSnapshot.GetStyle(
        ElementKey,
        _styleContextKeys,
        _styleAliasKeys);

    internal VectorScenePaintPalette CreatePaintPalette()
    {
        Color surface = Style.Background ?? LayoutContext.ThemeSnapshot.SurfaceColor;
        Color link = LayoutContext.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.Link,
            _styleContextKeys,
            _styleAliasKeys).Foreground;
        return new VectorScenePaintPalette(Style.Foreground, surface, link);
    }

    private bool HasCapacityFallbackText =>
        _capacityFallbackText.Length > 0 || _capacityFallbackFailureText.Length > 0;

    private static bool IsCapacityPreparationStatus(VectorNativePreparationStatus status) =>
        status is VectorNativePreparationStatus.DeferredCapacity or VectorNativePreparationStatus.TooLarge;

    internal float ResolveSceneUnitScale(ElementStyle containerStyle)
    {
        float contentScale = ResolveContentScale(
            _referenceFontSize,
            containerStyle.FontSize,
            LayoutContext.ThemeSnapshot.TextScaleFactor);
        double scaleX = _semanticViewport.Width > 0
            ? _sceneWidth * contentScale / _semanticViewport.Width
            : contentScale;
        double scaleY = _semanticViewport.Height > 0
            ? _sceneHeight * contentScale / _semanticViewport.Height
            : contentScale;
        double scale = Math.Min(scaleX, scaleY);
        return (float)Math.Clamp(double.IsFinite(scale) ? scale : contentScale, 0.01d, 100d);
    }

    internal void PaintScene(
        CanvasDrawingSession ds,
        VectorScenePaintPalette? paletteOverride = null)
    {
        Rect clip = HorizontalViewportBounds;
        VectorScenePaintPalette palette = paletteOverride ?? CreatePaintPalette();
        if (CanScrollHorizontally)
        {
            NativePaintSessionResources.LayerLease layer = LayoutContext.RenderResources.PaintSessions.CreateLayer(ds, 1f, clip);
            try
            {
                _drawing.Paint(LayoutContext.PlacementTransform, ds, _contentBounds, palette, LayoutContext.ThemeSnapshot.IsHighContrast);
            }
            catch (Exception primary)
            {
                layer.ClosePreservingPrimary(primary);
                throw;
            }
            layer.Close();
        }
        else
        {
            _drawing.Paint(LayoutContext.PlacementTransform, ds, _contentBounds, palette, LayoutContext.ThemeSnapshot.IsHighContrast);
        }
    }

    private Rect OuterBounds => new(
        Bounds.X + Margin.Left,
        Bounds.Y + Margin.Top,
        Math.Max(0, Bounds.Width - Margin.Left - Margin.Right),
        Math.Max(0, Bounds.Height - Margin.Top - Margin.Bottom));

    private void UpdateContentBounds()
    {
        double viewportLeft = Bounds.X + Margin.Left + Style.Padding.Left;
        double contentLeft = IsRightToLeft
            ? viewportLeft + _viewportWidth - _contentWidth + _horizontalOffset
            : viewportLeft - _horizontalOffset;
        if (!CanScrollHorizontally)
            contentLeft += Math.Max(0, (_viewportWidth - _contentWidth) / 2);
        _contentBounds = new Rect(
            contentLeft,
            Bounds.Y + Margin.Top + Style.Padding.Top,
            _contentWidth,
            _contentHeight);
    }

    internal void PaintHorizontalScrollbar(CanvasDrawingSession ds)
    {
        HorizontalOverflowVisual.Paint(
            ds,
            HorizontalScrollTrackBounds,
            HorizontalScrollThumbBounds,
            LayoutContext.ThemeSnapshot,
            Style.Foreground);
    }
}
