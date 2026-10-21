using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// A block-level painter-neutral scene with an intrinsic viewport. It owns only
/// local horizontal overflow; the document remains the sole vertical scroller.
/// </summary>
internal sealed class VectorSceneBox : BlockBox, IHorizontalOverflowBox, ISelectionBackgroundGeometrySource
{
    private const int AmbiguousBackgroundCommandIndex = -2;
    private const float HorizontalScrollbarHeight = 12f;
    private const float MinimumScrollbarThumbWidth = 24f;
    private const float NormalSelectionBackgroundOpacity = 0.25f;

    internal readonly MarkdownLayoutContext LayoutContext;
    private VectorSceneDrawing _drawing;
    private CanvasTextLayout? _detachedFallbackLayout;
    private bool _detachedFallbackCleanupPending;
    private NativeResourceRetirementRecord? _detachedFallbackRetirementRecord;
    private NativeResourceRetirementRecord? _detachedFallbackCandidateRecord;
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
            IndexSemanticCommands(scene, context.CancellationToken);
        _referenceFontSize = referenceFontSize;
        _styleContextKeys = context.CreateStyleContextSnapshot();
        _styleAliasKeys = context.CreateStyleAliasSnapshot();
        _drawing = new VectorSceneDrawing(
            context.ResourceCreator,
            scene,
            context.CancellationToken,
            Style.FontFamily
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            , context.LayoutDiagnostics
#endif
            , deferNativeResources: true,
            nativeAdmission: resolvedNativeAdmission);
        Margin = Style.Margin;
        _semanticTextStyles = BuildSemanticTextStyles(
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
        VectorSceneCapacityFallback.TryGetFormat(LayoutContext.FlowDirection, out _);

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

    /// <summary>Warms the borrowed process-wide fallback descriptor off the paint path.</summary>
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
        return VectorSceneCapacityFallback.PrepareFormat(LayoutContext.FlowDirection);
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
            DisposeDetachedFallback();
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
        DisposeDetachedFallback();
    }

    internal bool IsDisposed => _disposed;

    internal bool HasDetachedFallback =>
        (_detachedFallbackLayout is not null && !_detachedFallbackCleanupPending) ||
        (NativeAdmission is not null && IsGraphDetached && HasCapacityFallbackText &&
            VectorSceneCapacityFallback.TryGetFormat(LayoutContext.FlowDirection, out _));

    /// <summary>Transfers a staged layout; the caller disposes any rejected candidate.</summary>
    internal bool TryAdoptDetachedFallback(
        ref CanvasTextLayout? candidate,
        CancellationToken cancellationToken)
    {
        if (candidate is null)
            return false;
        NativeResourceRetirementRecord? candidateRecord = Volatile.Read(ref _detachedFallbackCandidateRecord);
        if (candidateRecord is null)
            return false;
        if (ReferenceEquals(_detachedFallbackLayout, candidate))
        {
            candidateRecord.SetTemporary(0, null);
            candidate = null;
            return false;
        }
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsGraphDetached)
            return false;
        // Do not sever the old layout's owner before successful cleanup. On a
        // failure the caller still owns its candidate and this box retains only
        // the old, non-paintable cleanup slot for a later retirement attempt.
        DisposeDetachedFallback();
        _detachedFallbackLayout = candidate;
        _detachedFallbackRetirementRecord = candidateRecord;
        candidate = null;
        return true;
    }

    internal bool TryBeginDetachedFallbackCandidate(NativeResourceRetirementRecord retirementRecord)
    {
        ArgumentNullException.ThrowIfNull(retirementRecord);
        if (_disposed || Interlocked.CompareExchange(
                ref _detachedFallbackCandidateRecord, retirementRecord, null) is not null)
            return false;
        if (!_disposed)
            return true;
        Interlocked.CompareExchange(ref _detachedFallbackCandidateRecord, null, retirementRecord);
        return false;
    }

    internal NativeResourceRetirementRecord? GetDetachedFallbackCandidateRecord() =>
        Volatile.Read(ref _detachedFallbackCandidateRecord);

    internal void EndDetachedFallbackCandidate(NativeResourceRetirementRecord retirementRecord) =>
        Interlocked.CompareExchange(ref _detachedFallbackCandidateRecord, null, retirementRecord);

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
            VectorSceneCapacityFallback.TryGetFormat(LayoutContext.FlowDirection, out _);
        if (ReferenceEquals(replacement, _drawing) || !replacement.HasScene ||
            (!replacementPrepared && !replacementCapacityFallbackReady))
        {
            throw new InvalidOperationException(
                "A replacement drawing must own a prepared scene or a prewarmed bounded-capacity fallback.");
        }

        DisposeDetachedFallback();
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

    public Rect HorizontalScrollTrackBounds
    {
        get
        {
            if (!CanScrollHorizontally)
                return Rect.Empty;
            Rect viewport = HorizontalViewportBounds;
            return new Rect(viewport.X, viewport.Bottom, viewport.Width, HorizontalScrollbarHeight);
        }
    }

    public Rect HorizontalScrollThumbBounds
    {
        get
        {
            Rect track = HorizontalScrollTrackBounds;
            if (track.IsEmpty || HorizontalExtent <= 0)
                return Rect.Empty;
            double thumbWidth = Math.Clamp(
                track.Width * HorizontalViewport / HorizontalExtent,
                Math.Min(MinimumScrollbarThumbWidth, track.Width),
                track.Width);
            double travel = Math.Max(0, track.Width - thumbWidth);
            double maximum = Math.Max(0, HorizontalExtent - HorizontalViewport);
            double fraction = maximum <= 0 ? 0 : HorizontalOffset / maximum;
            double x = IsRightToLeft
                ? track.Right - thumbWidth - travel * fraction
                : track.Left + travel * fraction;
            return new Rect(x, track.Y, thumbWidth, track.Height);
        }
    }

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
            height += HorizontalScrollbarHeight;
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
            PaintDetachedFallback(ds, Style.Foreground);
        else
            PaintCapacityFallback(ds, Style.Foreground);

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
    {
        if (SemanticItems.Count == 0)
        {
            // An unstructured scene has no semantic child index, but remains
            // one atomic document slot. -1 is reserved for semantic metadata;
            // pointer/document positions use the same zero-based slot as the
            // source map and selection-endpoint path.
            position = new DocumentPosition(BlockIndex, 0, point.X <= _contentBounds.Left ? 0 : 1);
            return Bounds.Contains(point);
        }

        if (TryGetSemanticAt(point, SemanticHitKind.Selectable, out int semanticIndex))
        {
            Rect semanticBounds = GetSemanticBounds(semanticIndex);
            position = new DocumentPosition(
                BlockIndex,
                semanticIndex,
                point.X <= semanticBounds.Left + semanticBounds.Width / 2 ? 0 : 1);
            return true;
        }

        position = DocumentPosition.Zero;
        return false;
    }

    internal Rect GetSemanticBounds(int semanticIndex)
    {
        // Semantic metadata survives command-graph retirement. Position still
        // comes from this box's current managed bounds; this is not an immutable
        // geometry snapshot or a replacement for layout read exclusion.
        if ((uint)semanticIndex >= (uint)SemanticItems.Count)
            return _contentBounds;
        VectorViewportTransform transform = VectorViewportTransform.Create(
            _semanticViewport,
            _contentBounds);
        return transform.Transform(SemanticItems[semanticIndex].Bounds);
    }

    internal Rect GetVisibleSemanticBounds(int semanticIndex) =>
        VectorViewportTransform.Intersect(
            GetSemanticBounds(semanticIndex),
            HorizontalViewportBounds);

    internal bool TryGetTextPositionAt(Point point, out DocumentPosition position)
    {
        if (TryGetSemanticAt(point, SemanticHitKind.Text, out int semanticIndex))
        {
            Rect semanticBounds = GetSemanticBounds(semanticIndex);
            position = new DocumentPosition(
                BlockIndex,
                semanticIndex,
                point.X <= semanticBounds.Left + semanticBounds.Width / 2 ? 0 : 1);
            return true;
        }

        position = DocumentPosition.Zero;
        return false;
    }

    internal override bool HitTestSelectionEndpoint(Point point, out DocumentPosition position)
    {
        if (!Bounds.Contains(point))
        {
            position = DocumentPosition.Zero;
            return false;
        }

        // A vector scene is one atomic source-map slot. During an active drag,
        // its upper and lower halves represent the boundary before and after the
        // scene respectively. This makes a final diagram selectable without
        // requiring a non-existent following block, while regular HitTest keeps
        // non-selectable diagram background inert for taps and drag starts.
        bool afterMidpoint = point.Y >= Bounds.Top + Bounds.Height / 2.0;
        position = new DocumentPosition(BlockIndex, 0, afterMidpoint ? 1 : 0);
        return true;
    }

    public override IEnumerable<Rect> GetSelectionRects(DocumentRange range)
    {
        if ((_drawing.IsPrepared || IsGraphDetached || IsCapacityFallbackPaintable) &&
            SelectionIntersectsAtomicSlot(range))
            yield return Bounds;
    }

    /// <summary>
    /// Returns paint-only selection geometry. The full block bounds remain
    /// authoritative for selection endpoints and point-in-selection tests;
    /// the adorner background follows only visible scene content.
    /// </summary>
    public IEnumerable<SelectionBackgroundGeometry> GetSelectionBackgroundGeometry(
        DocumentRange range)
    {
        if ((!_drawing.IsPrepared && !IsGraphDetached && !IsCapacityFallbackPaintable) ||
            !SelectionIntersectsAtomicSlot(range))
        {
            yield break;
        }

        Rect visibleContent = VisibleContentBounds;
        if (visibleContent.Width > 0 && visibleContent.Height > 0)
        {
            yield return new SelectionBackgroundGeometry(
                visibleContent,
                NormalSelectionBackgroundOpacity);
        }
    }

    public override void PaintSelectionForeground(
        CanvasDrawingSession ds,
        DocumentRange range,
        Color color,
        Rect viewport)
    {
        bool detached = IsGraphDetached;
        bool capacityFallback = !detached && IsCapacityFallbackPaintable;
        if ((!_drawing.IsPrepared && !detached && !capacityFallback) ||
            !SelectionIntersectsAtomicSlot(range) ||
            Bounds.Right < viewport.Left || Bounds.Left > viewport.Right ||
            Bounds.Bottom < viewport.Top || Bounds.Top > viewport.Bottom)
        {
            return;
        }

        Rect sceneViewport = HorizontalViewportBounds;
        if (sceneViewport.Width <= 0 || sceneViewport.Height <= 0)
            return;

        // The normal selection adorner supplies a translucent fill clipped to
        // VisibleContentBounds. Repaint the native scene at full opacity so
        // opaque glyphs and authored colors remain crisp while transparent
        // equation areas retain the underlying document. High Contrast keeps
        // the system Highlight/HighlightText pair at full opacity.
        bool highContrast = LayoutContext.ThemeSnapshot.IsHighContrast;
        Color fallbackForeground = highContrast ? color : Style.Foreground;
        VectorScenePaintPalette? selectedPalette = highContrast
            ? new VectorScenePaintPalette(
                color,
                LayoutContext.ThemeSnapshot.SelectionHighlightColor,
                color)
            : null;
        NativePaintSessionResources.LayerLease sceneLayer =
            NativePaintSessionResources.CreateLayer(ds, 1f, sceneViewport);
        try
        {
            if (_drawing.IsPrepared)
                PaintScene(ds, selectedPalette);
            else if (capacityFallback)
                PaintCapacityFallback(ds, fallbackForeground, clipAlreadyApplied: true);
            else if (HasDetachedFallback)
                PaintDetachedFallback(ds, fallbackForeground);
        }
        catch (Exception primary)
        {
            sceneLayer.ClosePreservingPrimary(primary);
            throw;
        }
        sceneLayer.Close();

        if (CanScrollHorizontally)
        {
            NativePaintSessionResources.LayerLease scrollbarLayer =
                NativePaintSessionResources.CreateLayer(ds, 0.82f, HorizontalScrollTrackBounds);
            try
            {
                PaintHorizontalScrollbar(ds);
            }
            catch (Exception primary)
            {
                scrollbarLayer.ClosePreservingPrimary(primary);
                throw;
            }
            scrollbarLayer.Close();
        }

        Rect outline = VisibleContentBounds;
        if (outline.Width > 0 && outline.Height > 0)
        {
            float radius = Math.Max(0, Style.CornerRadius);
            Color outlineColor = highContrast
                ? color
                : LayoutContext.ThemeSnapshot.SelectionHighlightColor;
            ds.DrawRoundedRectangle(outline, radius, radius, outlineColor, 2f);
        }
    }

    internal bool TryGetLinkAt(Point point, out MarkdownVectorLinkAction? link)
    {
        if (TryGetSemanticAt(point, SemanticHitKind.Linked, out int semanticIndex))
            return TryGetLink(semanticIndex, out link);

        link = null;
        return false;
    }

    internal bool TryGetLink(int semanticIndex, out MarkdownVectorLinkAction? link)
    {
        if ((uint)semanticIndex < (uint)SemanticItems.Count)
        {
            MarkdownVectorSemanticItem semantic = SemanticItems[semanticIndex];
            MarkdownVectorLinkAction? action = _semanticLinks[semanticIndex];
            if (MarkdownVectorSemanticPolicy.IsInvokable(semantic.Flags, action is not null))
            {
                link = action;
                return true;
            }
        }

        link = null;
        return false;
    }

    internal MarkdownVectorSemanticItem? GetSemanticItem(int semanticIndex) =>
        (uint)semanticIndex < (uint)SemanticItems.Count ? SemanticItems[semanticIndex] : null;

    internal CancellationToken CancellationToken => LayoutContext.CancellationToken;

    /// <summary>Returns the frozen draw-time typography and semantic paint projection.</summary>
    internal ElementStyle ResolveSemanticTextStyle(int semanticIndex) =>
        (uint)semanticIndex < (uint)_semanticTextStyles.Length
            ? _semanticTextStyles[semanticIndex]
            : Style;

    internal bool IsSelectableAt(Point point) =>
        SemanticItems.Count == 0
            ? Bounds.Contains(point)
            : TryGetSemanticAt(point, SemanticHitKind.Selectable, out _);

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
        _detachedFallbackCleanupPending = true;
        cleanup.TryDisposeAndClear(
            ref _detachedFallbackLayout,
            _detachedFallbackRetirementRecord?.Disposer);
        _detachedFallbackCleanupPending = _detachedFallbackLayout is not null;
        if (_detachedFallbackLayout is { } retainedFallback)
        {
            NativeResourceRetirementRecord? retirement = _detachedFallbackRetirementRecord;
            if (retirement is not null)
            {
                // Transfer the already-reserved native slot before releasing
                // this terminal owner. No queue node or callback is allocated.
                retirement.SetTemporary(0, retainedFallback);
                _detachedFallbackLayout = null;
                _detachedFallbackRetirementRecord = null;
                _disposed = true;
                retirement.Publish();
            }
            else
            {
                // Every adopted fallback is paired with a preallocated record.
                // Retain the field if that invariant is ever violated rather
                // than silently losing the only remaining native owner.
                _disposed = true;
            }
        }
        else
        {
            NativeResourceRetirementRecord? retirement = _detachedFallbackRetirementRecord;
            _detachedFallbackRetirementRecord = null;
            _disposed = true;
            if (retirement is not null)
            {
                retirement.SetTemporary(0, null);
                retirement.Publish();
            }
        }
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

    private ElementStyle Style => _style ??= LayoutContext.ThemeSnapshot.GetStyle(
        ElementKey,
        _styleContextKeys,
        _styleAliasKeys);

    private VectorScenePaintPalette CreatePaintPalette()
    {
        Color surface = Style.Background ?? LayoutContext.ThemeSnapshot.SurfaceColor;
        Color link = LayoutContext.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.Link,
            _styleContextKeys,
            _styleAliasKeys).Foreground;
        return new VectorScenePaintPalette(Style.Foreground, surface, link);
    }

    private void PaintDetachedFallback(CanvasDrawingSession drawingSession, Color foreground)
    {
        if (NativeAdmission is not null)
        {
            PaintCapacityFallback(drawingSession, foreground);
            return;
        }

        CanvasTextLayout? layout = _detachedFallbackLayout;
        if (layout is null)
            return;

        Rect clip = VectorViewportTransform.Intersect(_contentBounds, HorizontalViewportBounds);
        if (clip.Width <= 0 || clip.Height <= 0)
            return;

        Rect textBounds = layout.LayoutBounds;
        float left = (float)_contentBounds.X;
        float top = (float)(_contentBounds.Y + Math.Max(0, (_contentBounds.Height - textBounds.Height) / 2d));
        NativePaintSessionResources.LayerLease fallbackLayer =
            NativePaintSessionResources.CreateLayer(drawingSession, 1f, clip);
        try
        {
            PlacementAwareTextDrawing.DrawAtRootLocalOrigin(
                drawingSession,
                layout,
                left,
                top,
                foreground);
        }
        catch (Exception primary)
        {
            fallbackLayer.ClosePreservingPrimary(primary);
            throw;
        }
        fallbackLayer.Close();
    }

    private void PaintCapacityFallback(
        CanvasDrawingSession drawingSession,
        Color foreground,
        bool clipAlreadyApplied = false)
    {
        if (!VectorSceneCapacityFallback.TryGetFormat(
                LayoutContext.FlowDirection,
                out CanvasTextFormat format))
        {
            return;
        }

        string text = _graphRebindFailed ? _capacityFallbackFailureText : _capacityFallbackText;
        if (text.Length == 0)
            return;

        Rect clip = VectorViewportTransform.Intersect(_contentBounds, HorizontalViewportBounds);
        if (clip.Width <= 0 || clip.Height <= 0)
            return;

        if (clipAlreadyApplied)
        {
            VectorSceneCapacityFallback.DrawTextWithScale(
                drawingSession,
                text,
                _contentBounds,
                foreground,
                format,
                LayoutContext.ThemeSnapshot.TextScaleFactor);
            return;
        }

        NativePaintSessionResources.LayerLease fallbackLayer =
            NativePaintSessionResources.CreateLayer(drawingSession, 1f, clip);
        try
        {
            VectorSceneCapacityFallback.DrawTextWithScale(
                drawingSession,
                text,
                _contentBounds,
                foreground,
                format,
                LayoutContext.ThemeSnapshot.TextScaleFactor);
        }
        catch (Exception primary)
        {
            fallbackLayer.ClosePreservingPrimary(primary);
            throw;
        }
        fallbackLayer.Close();
    }

    private void DisposeDetachedFallback()
    {
        BlockBoxCleanupScope cleanup = default;
        _detachedFallbackCleanupPending = true;
        cleanup.TryDisposeAndClear(
            ref _detachedFallbackLayout,
            _detachedFallbackRetirementRecord?.Disposer);
        _detachedFallbackCleanupPending = _detachedFallbackLayout is not null;
        if (!_detachedFallbackCleanupPending)
            CompleteDetachedFallbackRetirement();
        cleanup.ThrowIfAny("disposing detached vector scene fallback");
    }

    private void CompleteDetachedFallbackRetirement()
    {
        NativeResourceRetirementRecord? retirement = _detachedFallbackRetirementRecord;
        if (retirement is null)
            return;

        retirement.SetTemporary(0, null);
        retirement.Publish();
        _detachedFallbackRetirementRecord = null;
    }

    private bool HasCapacityFallbackText =>
        _capacityFallbackText.Length > 0 || _capacityFallbackFailureText.Length > 0;

    private static bool IsCapacityPreparationStatus(VectorNativePreparationStatus status) =>
        status is VectorNativePreparationStatus.DeferredCapacity or VectorNativePreparationStatus.TooLarge;

    private float ResolveSceneUnitScale(ElementStyle containerStyle)
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

    private void PaintScene(
        CanvasDrawingSession ds,
        VectorScenePaintPalette? paletteOverride = null)
    {
        Rect clip = HorizontalViewportBounds;
        VectorScenePaintPalette palette = paletteOverride ?? CreatePaintPalette();
        if (CanScrollHorizontally)
        {
            NativePaintSessionResources.LayerLease layer = NativePaintSessionResources.CreateLayer(ds, 1f, clip);
            try
            {
                _drawing.Paint(ds, _contentBounds, palette, LayoutContext.ThemeSnapshot.IsHighContrast);
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
            _drawing.Paint(ds, _contentBounds, palette, LayoutContext.ThemeSnapshot.IsHighContrast);
        }
    }

    private bool SelectionIntersectsAtomicSlot(DocumentRange range)
    {
        DocumentRange normalized = range.Normalized();
        if (normalized.IsEmpty ||
            normalized.End.BlockIndex < BlockIndex ||
            normalized.Start.BlockIndex > BlockIndex)
        {
            return false;
        }

        var before = new DocumentPosition(BlockIndex, 0, 0);
        var after = new DocumentPosition(BlockIndex, 0, 1);
        if (normalized.Start.BlockIndex < BlockIndex)
            return normalized.End.BlockIndex > BlockIndex || normalized.End != before;
        if (normalized.End.BlockIndex > BlockIndex)
            return normalized.Start != after;

        // Vector semantic children use their semantic index as InlineIndex so
        // TextPattern can expose individual labels and nodes. Any non-empty
        // selection wholly inside this block therefore intersects the atomic
        // visual even when neither endpoint uses its source-map slot (0, 0..1).
        return true;
    }

    private ElementStyle[] BuildSemanticTextStyles(
        MarkdownVectorScene scene,
        int[] textCommandIndices,
        int[] backgroundCommandIndices)
    {
        int count = SemanticItems.Count;
        if (count == 0)
            return Array.Empty<ElementStyle>();

        ElementStyle containerStyle = Style;
        VectorScenePaintPalette palette = CreatePaintPalette();
        var styles = new ElementStyle[count];
        Color?[]? backgrounds = null;
        byte[]? backgroundStates = null;
        int[]? path = null;
        float sceneUnitScale = ResolveSceneUnitScale(containerStyle);
        for (int semanticIndex = 0; semanticIndex < count; semanticIndex++)
        {
            ThrowIfCancellationRequested();
            int textCommandIndex = textCommandIndices[semanticIndex];
            if (textCommandIndex < 0)
            {
                styles[semanticIndex] = containerStyle;
                continue;
            }

            backgrounds ??= new Color?[count];
            backgroundStates ??= new byte[count];
            path ??= new int[count];
            Color? background = ResolveSemanticBackgroundColor(
                scene,
                semanticIndex,
                backgroundCommandIndices,
                backgrounds,
                backgroundStates,
                path,
                containerStyle.Background,
                palette);
            styles[semanticIndex] = BuildSemanticTextStyle(
                scene,
                semanticIndex,
                textCommandIndex,
                containerStyle,
                background,
                sceneUnitScale,
                palette);
        }

        return styles;
    }

    private ElementStyle BuildSemanticTextStyle(
        MarkdownVectorScene scene,
        int semanticIndex,
        int commandIndex,
        ElementStyle containerStyle,
        Color? effectiveBackground,
        float sceneUnitScale,
        VectorScenePaintPalette palette)
    {
        MarkdownVectorCommand command = scene.Commands[commandIndex];
        MarkdownVectorPaintStyle paintStyle = command.Style!;
        MarkdownVectorTextStyle textStyle = command.TextStyle!;
        uint? textArgb = paintStyle.FillArgb ?? paintStyle.StrokeArgb;
        MarkdownVectorPaintRole declaredRole =
            paintStyle.FillArgb is not null || paintStyle.StrokeArgb is null
                ? paintStyle.FillRole
                : paintStyle.StrokeRole;
        MarkdownVectorPaintRole highContrastRole =
            paintStyle.FillArgb is not null || paintStyle.StrokeArgb is null
                ? paintStyle.HighContrastFillRole ?? MarkdownVectorPaintRole.Foreground
                : paintStyle.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground;
        bool linkedSemantic = _semanticLinks[semanticIndex] is not null;
        MarkdownVectorPaintRole effectiveRole = VectorSceneDrawing.ResolvePaintRole(
            command,
            declaredRole,
            textArgb,
            highContrastRole,
            LayoutContext.ThemeSnapshot.IsHighContrast,
            linkedSemantic);
        Color effectiveForeground = VectorSceneDrawing.ResolveColor(
            textArgb,
            paintStyle.Opacity,
            palette,
            effectiveRole,
            LayoutContext.ThemeSnapshot.IsHighContrast);

        return new ElementStyle
        {
            FontFamily = VectorSceneDrawing.ResolveFontFamily(textStyle, containerStyle.FontFamily),
            FontSize = Math.Max(1f, textStyle.FontSize * sceneUnitScale),
            FontWeight = new Windows.UI.Text.FontWeight { Weight = textStyle.FontWeight },
            FontStyle = textStyle.Italic
                ? Windows.UI.Text.FontStyle.Italic
                : Windows.UI.Text.FontStyle.Normal,
            Foreground = effectiveForeground,
            HoverForeground = containerStyle.HoverForeground,
            FocusForeground = containerStyle.FocusForeground,
            Background = effectiveBackground,
            AccentBar = containerStyle.AccentBar,
            BorderBrush = containerStyle.BorderBrush,
            BorderThickness = containerStyle.BorderThickness,
            CornerRadius = containerStyle.CornerRadius,
            ListIndent = containerStyle.ListIndent,
            NestedListIndent = containerStyle.NestedListIndent,
            Underline = containerStyle.Underline,
            Strikethrough = containerStyle.Strikethrough,
            Margin = containerStyle.Margin,
            Padding = containerStyle.Padding,
            LineHeightMultiplier = containerStyle.LineHeightMultiplier,
        };
    }

    private static (int[] Text, int[] Background, float ReferenceFontSize) IndexSemanticCommands(
        MarkdownVectorScene scene,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int semanticCount = scene.Semantics.Count;
        if (semanticCount == 0 && scene.ReferenceFontSize > 0)
        {
            return (Array.Empty<int>(), Array.Empty<int>(), scene.ReferenceFontSize);
        }

        var textIndices = semanticCount == 0 ? Array.Empty<int>() : new int[semanticCount];
        var backgroundIndices = semanticCount == 0 ? Array.Empty<int>() : new int[semanticCount];
        var ambiguousBackgrounds = semanticCount == 0 ? Array.Empty<bool>() : new bool[semanticCount];
        Array.Fill(textIndices, -1);
        Array.Fill(backgroundIndices, -1);
        double weightedTextSize = 0;
        long textUnits = 0;
        bool inferReferenceFontSize = scene.ReferenceFontSize <= 0;
        for (int i = 0; i < scene.Commands.Count; i++)
        {
            if ((i & 0x3F) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            MarkdownVectorCommand command = scene.Commands[i];
            if (inferReferenceFontSize &&
                command.Kind == MarkdownVectorCommandKind.DrawText &&
                command.TextStyle is { FontSize: > 0 } textStyle)
            {
                int weight = Math.Max(1, command.Text?.Length ?? 0);
                weightedTextSize += textStyle.FontSize * weight;
                textUnits += weight;
            }

            if ((uint)command.SemanticIndex >= (uint)textIndices.Length)
                continue;

            int semanticIndex = command.SemanticIndex;
            if (command.Kind == MarkdownVectorCommandKind.DrawText &&
                command.Style is not null &&
                command.TextStyle is not null &&
                textIndices[semanticIndex] < 0)
            {
                textIndices[semanticIndex] = i;
            }

            if (!TryGetVisibleBackgroundStyle(
                    command,
                    scene.Semantics[semanticIndex].Role,
                    out MarkdownVectorPaintStyle style))
            {
                continue;
            }

            if (ambiguousBackgrounds[semanticIndex])
                continue;
            if (backgroundIndices[semanticIndex] < 0)
            {
                backgroundIndices[semanticIndex] = i;
                continue;
            }

            MarkdownVectorPaintStyle existing =
                scene.Commands[backgroundIndices[semanticIndex]].Style!;
            if (!HaveEquivalentFill(existing, style))
            {
                ambiguousBackgrounds[semanticIndex] = true;
                backgroundIndices[semanticIndex] = AmbiguousBackgroundCommandIndex;
            }
        }

        float referenceFontSize = scene.ReferenceFontSize > 0
            ? scene.ReferenceFontSize
            : textUnits > 0
                ? (float)(weightedTextSize / textUnits)
                : 0;
        return (textIndices, backgroundIndices, referenceFontSize);
    }

    private Color? ResolveSemanticBackgroundColor(
        MarkdownVectorScene scene,
        int semanticIndex,
        int[] backgroundCommandIndices,
        Color?[] resolvedBackgrounds,
        byte[] resolvedStates,
        int[] path,
        Color? fallback,
        VectorScenePaintPalette palette)
    {
        int current = semanticIndex;
        int pathCount = 0;
        Color? background = fallback;
        while ((uint)current < (uint)SemanticItems.Count)
        {
            if (resolvedStates[current] != 0)
            {
                background = resolvedBackgrounds[current];
                break;
            }

            int commandIndex = backgroundCommandIndices[current];
            if (commandIndex == AmbiguousBackgroundCommandIndex)
            {
                resolvedBackgrounds[current] = fallback;
                resolvedStates[current] = 1;
                background = fallback;
                break;
            }
            if (commandIndex >= 0)
            {
                MarkdownVectorCommand command = scene.Commands[commandIndex];
                MarkdownVectorPaintStyle style = command.Style!;
                MarkdownVectorPaintRole role = VectorSceneDrawing.ResolvePaintRole(
                    command,
                    style.FillRole,
                    style.FillArgb,
                    style.HighContrastFillRole ?? VectorSceneDrawing.InferFillRole(
                        command,
                        style,
                        SemanticItems[current].Role),
                    LayoutContext.ThemeSnapshot.IsHighContrast,
                    _semanticLinks[current] is not null);
                background = VectorSceneDrawing.ResolveColor(
                    style.FillArgb,
                    style.Opacity,
                    palette,
                    role,
                    LayoutContext.ThemeSnapshot.IsHighContrast);
                resolvedBackgrounds[current] = background;
                resolvedStates[current] = 1;
                break;
            }

            path[pathCount++] = current;
            if ((pathCount & 0x3F) == 0)
                ThrowIfCancellationRequested();
            current = SemanticItems[current].ParentIndex;
        }

        while (pathCount > 0)
        {
            int pathIndex = path[--pathCount];
            resolvedBackgrounds[pathIndex] = background;
            resolvedStates[pathIndex] = 1;
        }

        return background;
    }

    private static bool TryGetVisibleBackgroundStyle(
        MarkdownVectorCommand command,
        MarkdownVectorSemanticRole semanticRole,
        out MarkdownVectorPaintStyle style)
    {
        if (command.Kind is not (MarkdownVectorCommandKind.DrawRectangle or
            MarkdownVectorCommandKind.DrawEllipse or
            MarkdownVectorCommandKind.DrawPath) ||
            command.Style is not { Opacity: >= 0.999f } candidate)
        {
            style = null!;
            return false;
        }

        style = candidate;
        bool opaque = style.FillArgb is not { } fill || (fill >> 24) == 0xFF;
        MarkdownVectorPaintRole highContrastRole = style.HighContrastFillRole ??
            (style.FillRole == MarkdownVectorPaintRole.Surface
                ? MarkdownVectorPaintRole.Surface
                : VectorSceneDrawing.InferFillRole(command, style, semanticRole));
        return opaque && highContrastRole == MarkdownVectorPaintRole.Surface;
    }

    private static bool HaveEquivalentFill(
        MarkdownVectorPaintStyle left,
        MarkdownVectorPaintStyle right) =>
        left.FillArgb == right.FillArgb &&
        left.FillRole == right.FillRole &&
        left.HighContrastFillRole == right.HighContrastFillRole &&
        Math.Abs(left.Opacity - right.Opacity) < 0.001f;

    private bool TryGetSemanticAt(
        Point point,
        SemanticHitKind kind,
        out int semanticIndex)
    {
        semanticIndex = -1;
        if (!HorizontalViewportBounds.Contains(point))
            return false;

        int bestDepth = -1;
        double bestArea = double.PositiveInfinity;
        ReadOnlySpan<int> semanticDepths = _semanticDepths.Span;
        for (int i = 0; i < SemanticItems.Count; i++)
        {
            MarkdownVectorSemanticItem semantic = SemanticItems[i];
            bool eligible = kind switch
            {
                SemanticHitKind.Selectable => MarkdownVectorSemanticPolicy.IsSelectable(semantic.Flags),
                SemanticHitKind.Linked => MarkdownVectorSemanticPolicy.IsInvokable(
                    semantic.Flags,
                    _semanticLinks[i] is not null),
                SemanticHitKind.Text => MarkdownVectorSemanticPolicy.IsExposed(semantic.Flags) &&
                                        !string.IsNullOrWhiteSpace(semantic.Name) &&
                                        !(semantic.ParentIndex < 0 &&
                                          semantic.Role == MarkdownVectorSemanticRole.Diagram),
                _ => false,
            };
            if (!eligible)
                continue;

            Rect bounds = GetSemanticBounds(i);
            if (!bounds.Contains(point))
                continue;

            int depth = semanticDepths[i];
            double area = bounds.Width * bounds.Height;
            if (depth < bestDepth || (depth == bestDepth && area > bestArea))
                continue;

            semanticIndex = i;
            bestDepth = depth;
            bestArea = area;
        }

        return semanticIndex >= 0;
    }

    private enum SemanticHitKind
    {
        Selectable,
        Linked,
        Text,
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

    private void PaintHorizontalScrollbar(CanvasDrawingSession ds)
    {
        HorizontalOverflowVisual.Paint(
            ds,
            HorizontalScrollTrackBounds,
            HorizontalScrollThumbBounds,
            LayoutContext.ThemeSnapshot,
            Style.Foreground);
    }
}
