using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
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
internal sealed class VectorSceneBox : BlockBox, IHorizontalOverflowBox
{
    private const int AmbiguousBackgroundCommandIndex = -2;
    private const float HorizontalScrollbarHeight = 12f;
    private const float MinimumScrollbarThumbWidth = 24f;

    private readonly MarkdownLayoutContext _context;
    private readonly MarkdownVectorScene _scene;
    private readonly VectorSceneDrawing _drawing;
    private readonly string _elementKey;
    private readonly float _referenceFontSize;
    private readonly int[] _semanticTextCommandIndices;
    private readonly int[] _semanticBackgroundCommandIndices;
    private readonly IReadOnlyList<string> _styleContextKeys;
    private readonly IReadOnlyList<string> _styleAliasKeys;
    private ElementStyle? _style;
    private float _viewportWidth;
    private float _contentWidth;
    private float _contentHeight;
    private double _horizontalOffset;
    private Rect _contentBounds;

    internal VectorSceneBox(
        MarkdownLayoutContext context,
        MarkdownContent content,
        string elementKey)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Content = content ?? throw new ArgumentNullException(nameof(content));
        _scene = content.VectorScene ?? throw new ArgumentException("Vector content requires a scene.", nameof(content));
        _elementKey = string.IsNullOrWhiteSpace(elementKey) ? MarkdownElementKeys.Diagram : elementKey;
        _referenceFontSize = ResolveReferenceFontSize(_scene);
        (_semanticTextCommandIndices, _semanticBackgroundCommandIndices) =
            IndexSemanticCommands(_scene);
        _styleContextKeys = context.CreateStyleContextSnapshot();
        _styleAliasKeys = context.CreateStyleAliasSnapshot();
        _drawing = new VectorSceneDrawing(
            context.ResourceCreator,
            _scene,
            context.CancellationToken,
            Style.FontFamily);
        Margin = Style.Margin;
    }

    internal MarkdownContent Content { get; }

    internal MarkdownVectorScene Scene => _scene;

    internal Rect ContentBounds => _contentBounds;

    internal Rect VisibleContentBounds =>
        VectorViewportTransform.Intersect(_contentBounds, HorizontalViewportBounds);

    internal string ElementKey => _elementKey;

    internal ElementStyle ResolvedStyle => Style;

    public double HorizontalOffset => _horizontalOffset;

    public double HorizontalExtent => Math.Max(_contentWidth, _viewportWidth);

    public double HorizontalViewport => _viewportWidth;

    public bool CanScrollHorizontally => HorizontalExtent > HorizontalViewport + 0.5;

    public bool IsRightToLeft => _context.FlowDirection == Microsoft.UI.Xaml.FlowDirection.RightToLeft;

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
        ThrowIfCancellationRequested();
        Margin = Style.Margin;
        _viewportWidth = (float)Math.Max(
            1,
            availableWidth - Margin.Left - Margin.Right - Style.Padding.Left - Style.Padding.Right);
        float scale = ResolveContentScale(
            _referenceFontSize,
            Style.FontSize,
            _context.ThemeSnapshot.TextScaleFactor);
        _contentWidth = Math.Max(1, _scene.Width * scale);
        _contentHeight = Math.Max(1, _scene.Height * scale);
        _horizontalOffset = Math.Clamp(_horizontalOffset, 0, Math.Max(0, HorizontalExtent - HorizontalViewport));
        double height = Margin.Top + Style.Padding.Top + _contentHeight + Style.Padding.Bottom + Margin.Bottom;
        if (CanScrollHorizontally)
            height += HorizontalScrollbarHeight;
        Bounds = new Rect(0, 0, availableWidth, height);
        UpdateContentBounds();
        return (float)height;
    }

    public override void Arrange(float x, float y, float width)
    {
        Bounds = new Rect(x, y, width, Bounds.Height);
        UpdateContentBounds();
        IsDirty = false;
    }

    internal override void ThrowIfCancellationRequested() =>
        _context.CancellationToken.ThrowIfCancellationRequested();

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        Rect outer = OuterBounds;
        if (outer.Width <= 0 || outer.Height <= 0)
            return;
        float radius = Math.Max(0, Style.CornerRadius);
        if (Style.Background is { } background)
            ds.FillRoundedRectangle(outer, radius, radius, background);

        PaintScene(ds);

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
        if (_scene.Semantics.Count == 0)
        {
            position = new DocumentPosition(BlockIndex, -1, point.X <= _contentBounds.Left ? 0 : 1);
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
        if ((uint)semanticIndex >= (uint)_scene.Semantics.Count)
            return _contentBounds;
        VectorViewportTransform transform = VectorViewportTransform.Create(
            _scene.Viewport,
            _contentBounds);
        return transform.Transform(_scene.Semantics[semanticIndex].Bounds);
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
        if (SelectionIntersectsAtomicSlot(range))
            yield return Bounds;
    }

    public override void PaintSelectionForeground(
        CanvasDrawingSession ds,
        DocumentRange range,
        Color color,
        Rect viewport)
    {
        if (!SelectionIntersectsAtomicSlot(range) ||
            Bounds.Right < viewport.Left || Bounds.Left > viewport.Right ||
            Bounds.Bottom < viewport.Top || Bounds.Top > viewport.Bottom)
        {
            return;
        }

        Rect sceneViewport = HorizontalViewportBounds;
        if (sceneViewport.Width <= 0 || sceneViewport.Height <= 0)
            return;

        // Match block images: the opaque selection fill remains visible as a
        // tint, then the native scene is repainted above it so equations, graph
        // edges, labels, and authored colors remain legible. High Contrast uses
        // the system Highlight/HighlightText pair at full opacity; retaining
        // normal scene colors or translucency can make selected content vanish.
        bool highContrast = _context.ThemeSnapshot.IsHighContrast;
        VectorScenePaintPalette? selectedPalette = highContrast
            ? new VectorScenePaintPalette(
                color,
                _context.ThemeSnapshot.SelectionHighlightColor,
                color)
            : null;
        using (ds.CreateLayer(highContrast ? 1f : 0.82f, sceneViewport))
            PaintScene(ds, selectedPalette);

        if (CanScrollHorizontally)
        {
            using (ds.CreateLayer(0.82f, HorizontalScrollTrackBounds))
                PaintHorizontalScrollbar(ds);
        }

        Rect outline = VisibleContentBounds;
        if (outline.Width > 0 && outline.Height > 0)
        {
            float radius = Math.Max(0, Style.CornerRadius);
            ds.DrawRoundedRectangle(outline, radius, radius, color, 2f);
        }
    }

    internal bool TryGetLinkAt(Point point, out MarkdownVectorLinkAction? link)
    {
        if (TryGetSemanticAt(point, SemanticHitKind.Linked, out int semanticIndex) &&
            _scene.GetLinkAction(semanticIndex) is { } action)
        {
            link = action;
            return true;
        }

        link = null;
        return false;
    }

    internal bool TryGetLink(int semanticIndex, out MarkdownVectorLinkAction? link)
    {
        if ((uint)semanticIndex < (uint)_scene.Semantics.Count)
        {
            MarkdownVectorSemanticItem semantic = _scene.Semantics[semanticIndex];
            MarkdownVectorLinkAction? action = _scene.GetLinkAction(semanticIndex);
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
        (uint)semanticIndex < (uint)_scene.Semantics.Count ? _scene.Semantics[semanticIndex] : null;

    /// <summary>
    /// Projects the immutable scene's effective draw-time text attributes into
    /// UI Automation. The containing diagram style remains the semantic style
    /// context, while command typography and semantic paint roles match what
    /// <see cref="VectorSceneDrawing"/> actually paints.
    /// </summary>
    internal ElementStyle ResolveSemanticTextStyle(int semanticIndex)
    {
        ElementStyle containerStyle = Style;
        if ((uint)semanticIndex >= (uint)_semanticTextCommandIndices.Length ||
            _semanticTextCommandIndices[semanticIndex] < 0)
        {
            return containerStyle;
        }

        MarkdownVectorCommand command = _scene.Commands[_semanticTextCommandIndices[semanticIndex]];
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
        bool linkedSemantic = _scene.GetLinkAction(semanticIndex) is not null;
        MarkdownVectorPaintRole effectiveRole = VectorSceneDrawing.ResolvePaintRole(
            command,
            declaredRole,
            textArgb,
            highContrastRole,
            _context.ThemeSnapshot.IsHighContrast,
            linkedSemantic);
        Color effectiveForeground = VectorSceneDrawing.ResolveColor(
            textArgb,
            paintStyle.Opacity,
            CreatePaintPalette(),
            effectiveRole,
            _context.ThemeSnapshot.IsHighContrast);
        Color? effectiveBackground = ResolveSemanticBackgroundColor(
            semanticIndex,
            containerStyle.Background);
        float sceneUnitScale = ResolveSceneUnitScale(containerStyle);

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

    internal bool IsSelectableAt(Point point) =>
        _scene.Semantics.Count == 0
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

    public override void Dispose() => _drawing.Dispose();

    internal static float ResolveContentScale(
        MarkdownVectorScene scene,
        float resolvedStyleFontSize,
        double textScaleFactor)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return ResolveContentScale(
            ResolveReferenceFontSize(scene),
            resolvedStyleFontSize,
            textScaleFactor);
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

    private static float ResolveReferenceFontSize(MarkdownVectorScene scene) =>
        scene.ReferenceFontSize > 0
            ? scene.ReferenceFontSize
            : ResolveRepresentativeTextSize(scene);

    private ElementStyle Style => _style ??= _context.ThemeSnapshot.GetStyle(
        _elementKey,
        _styleContextKeys,
        _styleAliasKeys);

    private VectorScenePaintPalette CreatePaintPalette()
    {
        Color surface = Style.Background ?? _context.ThemeSnapshot.SurfaceColor;
        Color link = _context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.Link,
            _styleContextKeys,
            _styleAliasKeys).Foreground;
        return new VectorScenePaintPalette(Style.Foreground, surface, link);
    }

    private float ResolveSceneUnitScale(ElementStyle containerStyle)
    {
        float contentScale = ResolveContentScale(
            _referenceFontSize,
            containerStyle.FontSize,
            _context.ThemeSnapshot.TextScaleFactor);
        double scaleX = _scene.Viewport.Width > 0
            ? _scene.Width * contentScale / _scene.Viewport.Width
            : contentScale;
        double scaleY = _scene.Viewport.Height > 0
            ? _scene.Height * contentScale / _scene.Viewport.Height
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
            using CanvasActiveLayer layer = ds.CreateLayer(1f, clip);
            _drawing.Paint(ds, _contentBounds, palette, _context.ThemeSnapshot.IsHighContrast);
        }
        else
        {
            _drawing.Paint(ds, _contentBounds, palette, _context.ThemeSnapshot.IsHighContrast);
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

    private static (int[] Text, int[] Background) IndexSemanticCommands(
        MarkdownVectorScene scene)
    {
        var textIndices = new int[scene.Semantics.Count];
        var backgroundIndices = new int[scene.Semantics.Count];
        var ambiguousBackgrounds = new bool[scene.Semantics.Count];
        Array.Fill(textIndices, -1);
        Array.Fill(backgroundIndices, -1);
        for (int i = 0; i < scene.Commands.Count; i++)
        {
            MarkdownVectorCommand command = scene.Commands[i];
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

        return (textIndices, backgroundIndices);
    }

    private Color? ResolveSemanticBackgroundColor(int semanticIndex, Color? fallback)
    {
        int current = semanticIndex;
        while ((uint)current < (uint)_semanticBackgroundCommandIndices.Length)
        {
            int commandIndex = _semanticBackgroundCommandIndices[current];
            if (commandIndex == AmbiguousBackgroundCommandIndex)
                return fallback;
            if (commandIndex >= 0)
            {
                MarkdownVectorCommand command = _scene.Commands[commandIndex];
                MarkdownVectorPaintStyle style = command.Style!;
                MarkdownVectorPaintRole role = VectorSceneDrawing.ResolvePaintRole(
                    command,
                    style.FillRole,
                    style.FillArgb,
                    style.HighContrastFillRole ?? VectorSceneDrawing.InferFillRole(
                        command,
                        style,
                        _scene.Semantics[current].Role),
                    _context.ThemeSnapshot.IsHighContrast,
                    _scene.GetLinkAction(current) is not null);
                return VectorSceneDrawing.ResolveColor(
                    style.FillArgb,
                    style.Opacity,
                    CreatePaintPalette(),
                    role,
                    _context.ThemeSnapshot.IsHighContrast);
            }

            current = _scene.Semantics[current].ParentIndex;
        }

        return fallback;
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

    private static float ResolveRepresentativeTextSize(MarkdownVectorScene scene)
    {
        double weightedSize = 0;
        long textUnits = 0;
        for (int i = 0; i < scene.Commands.Count; i++)
        {
            MarkdownVectorCommand command = scene.Commands[i];
            if (command.Kind != MarkdownVectorCommandKind.DrawText ||
                command.TextStyle is not { FontSize: > 0 } textStyle)
            {
                continue;
            }

            int weight = Math.Max(1, command.Text?.Length ?? 0);
            weightedSize += textStyle.FontSize * weight;
            textUnits += weight;
        }

        return textUnits > 0 ? (float)(weightedSize / textUnits) : 0;
    }

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
        for (int i = 0; i < _scene.Semantics.Count; i++)
        {
            MarkdownVectorSemanticItem semantic = _scene.Semantics[i];
            bool eligible = kind switch
            {
                SemanticHitKind.Selectable => MarkdownVectorSemanticPolicy.IsSelectable(semantic.Flags),
                SemanticHitKind.Linked => MarkdownVectorSemanticPolicy.IsInvokable(
                    semantic.Flags,
                    _scene.GetLinkAction(i) is not null),
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

            int depth = _scene.GetSemanticDepth(i);
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
            _context.ThemeSnapshot,
            Style.Foreground);
    }
}
