using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Stacks child blocks vertically with optional left indent &amp; accent bar (used
/// for blockquotes &amp; list items).
/// </summary>
internal partial class StackBox : BlockBox
{
    private readonly SingletonMutableList<BlockBox> _children = new();
    private double[] _childBottomEdges = Array.Empty<double>();
    private BlockBox? _collapsedTrailingChild;
    private Thickness _collapsedTrailingChildOriginalMargin;
    private double _trailingChildMarginOutsideChrome;
    public IReadOnlyList<BlockBox> Children => _children;

    /// <summary>
    /// True when the safe-HTML renderer has resolved inherited and local
    /// alignment for this subtree. Ancestor Markdown scopes must not flatten
    /// those per-descendant results afterward.
    /// </summary>
    internal bool HasResolvedSafeHtmlAlignment { get; set; }

    internal static bool SharesSafeHtmlInlineFlow(BlockBox previous, BlockBox next)
    {
        if (previous.SafeHtmlInlineFlowScopeId is not int scopeId)
        {
            return false;
        }

        return next.SafeHtmlInlineFlowScopeId == scopeId;
    }

    public Thickness ContentPadding { get; set; }
    public Windows.UI.Color? AccentBar { get; set; }
    internal float AccentBarThickness { get; set; } = 3f;
    internal bool AccentBarAffectsContentLayout { get; set; }
    public Windows.UI.Color? Background { get; set; }
    public Windows.UI.Color? BorderBrush { get; set; }
    public float BorderThickness { get; set; }
    public float CornerRadius { get; set; } = 0;

    /// <summary>
    /// Additional vertical space between children. Tight Markdown lists use
    /// this for sibling separation without adding space after the final item.
    /// </summary>
    internal float InterChildSpacing { get; set; }

    /// <summary>
    /// Collapses positive adjacent child margins for the safe-HTML normal-flow
    /// stacks that explicitly opt in. Other stacks retain additive margins.
    /// </summary>
    internal bool CollapsePositiveAdjacentMargins { get; set; }

    /// <summary>
    /// Flow direction for this stack. When RightToLeft, the accent bar (used
    /// for blockquotes / GFM alerts) is drawn on the right edge instead of
    /// the left, matching RTL reading order.
    /// </summary>
    public FlowDirection FlowDirection { get; set; } = FlowDirection.LeftToRight;

    // This clone path is deliberately limited to ordinary Markdown stacks.
    // Safe-HTML stacks can carry inherited alignment/flow relationships and a
    // collapsed trailing margin whose original child input is no longer
    // recoverable without mutating the published source tree.
    internal bool IsPrivateRasterPlacementCloneEligible =>
        GetType() == typeof(StackBox) &&
        SafeHtmlInlineFlowScopeId is null &&
        !HasResolvedSafeHtmlAlignment &&
        !CollapsePositiveAdjacentMargins &&
        _collapsedTrailingChild is null &&
        _trailingChildMarginOutsideChrome == 0;

    internal StackBox CreatePrivateRasterPlacementCloneShell()
    {
        if (!IsPrivateRasterPlacementCloneEligible)
            throw new InvalidOperationException("This stack carries state that cannot be privately cloned for raster placement.");

        var clone = new StackBox
        {
            ContentPadding = ContentPadding,
            AccentBar = AccentBar,
            AccentBarThickness = AccentBarThickness,
            AccentBarAffectsContentLayout = AccentBarAffectsContentLayout,
            Background = Background,
            BorderBrush = BorderBrush,
            BorderThickness = BorderThickness,
            CornerRadius = CornerRadius,
            InterChildSpacing = InterChildSpacing,
            CollapsePositiveAdjacentMargins = CollapsePositiveAdjacentMargins,
            FlowDirection = FlowDirection,
            BlockIndex = BlockIndex,
        };
        clone.CopyPlacementMarginsFrom(this);
        return clone;
    }

    public void Add(BlockBox child) => _children.Add(child);

    /// <summary>
    /// Removes all children from this temporary construction owner so they
    /// can be attached to their published parent without leaving aliases that
    /// would dispose the same resources twice. This is intended for an
    /// unmeasured staging stack; any collapsed margin adjustment is restored
    /// before ownership moves.
    /// </summary>
    internal BlockBox[] DetachChildrenForTransfer()
    {
        if (_children.Count == 0)
            return Array.Empty<BlockBox>();

        BlockBox[] detached = _children.ToArray();
        RestoreCollapsedTrailingChildMargin();
        _children.Clear();
        _childBottomEdges = Array.Empty<double>();
        _trailingChildMarginOutsideChrome = 0;
        return detached;
    }

    internal void RememberCollapsedTrailingChildMargin(BlockBox child, Thickness originalMargin)
    {
        _collapsedTrailingChild = child;
        _collapsedTrailingChildOriginalMargin = originalMargin;
    }

    internal void RestoreCollapsedTrailingChildMargin()
    {
        if (_collapsedTrailingChild is not { } child)
            return;

        child.Margin = _collapsedTrailingChildOriginalMargin;
        _trailingChildMarginOutsideChrome = double.IsFinite(_collapsedTrailingChildOriginalMargin.Bottom)
            ? Math.Max(0, _collapsedTrailingChildOriginalMargin.Bottom)
            : 0;
        _collapsedTrailingChild = null;
    }

    public override float Measure(float availableWidth)
    {
        float accentBarInset = GetAccentBarContentInset();
        float innerWidth = availableWidth - (float)(ContentPadding.Left + ContentPadding.Right) -
            (float)(Margin.Left + Margin.Right) - accentBarInset;
        float y = (float)(Margin.Top + ContentPadding.Top);
        // In RTL, the accent bar is on the right edge, so indent content from the
        // RIGHT (ContentPadding.Left is the bar-side indent). Use ContentPadding.Right
        // as the child's left-edge offset, leaving ContentPadding.Left space on the right.
        float childStartX = FlowDirection == FlowDirection.RightToLeft
            ? (float)(Margin.Left + ContentPadding.Right)
            : (float)(Margin.Left + ContentPadding.Left + accentBarInset);
        float interChildSpacing = float.IsFinite(InterChildSpacing)
            ? Math.Max(0, InterChildSpacing)
            : 0;
        double previousChildBottomMargin = 0;
        for (int index = 0; index < _children.Count; index++)
        {
            BlockBox child = _children[index];
            child.ThrowIfCancellationRequested();
            float h = child.Measure(innerWidth);
            if (index > 0)
            {
                y += interChildSpacing;
                if (CollapsePositiveAdjacentMargins)
                {
                    y -= (float)GetPositiveCollapsedMargin(previousChildBottomMargin, child.Margin.Top);
                }
            }
            child.Arrange(childStartX, y, innerWidth);
            y += h;
            previousChildBottomMargin = child.Margin.Bottom;
        }
        y += (float)(ContentPadding.Bottom + Margin.Bottom);
        Bounds = new Rect(0, 0, availableWidth, y);
        RefreshChildBottomEdges();
        return y;
    }

    internal override void ThrowIfCancellationRequested()
    {
        foreach (var child in _children)
            child.ThrowIfCancellationRequested();
    }

    public override void Arrange(float x, float y, float width)
    {
        float dx = x - (float)Bounds.X;
        float dy = y - (float)Bounds.Y;
        foreach (var c in _children)
            c.Arrange((float)c.Bounds.X + dx, (float)c.Bounds.Y + dy, (float)c.Bounds.Width);
        Bounds = new Rect(x, y, width, Bounds.Height);
        RefreshChildBottomEdges();
        IsDirty = false;
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        if (Background is { } bg)
        {
            var rect = new Rect(Bounds.X + Margin.Left, Bounds.Y + Margin.Top,
                                Bounds.Width - Margin.Left - Margin.Right,
                                GetChromeHeight());
            ds.FillRoundedRectangle(rect, CornerRadius, CornerRadius, bg);
        }
        if (BorderBrush is { } border && BorderThickness > 0)
        {
            var rect = new Rect(Bounds.X + Margin.Left, Bounds.Y + Margin.Top,
                                Bounds.Width - Margin.Left - Margin.Right,
                                GetChromeHeight());
            float nnset = BorderThickness / 2f;
            ds.DrawRoundedRectangle(
                new Rect(
                    rect.X + nnset,
                    rect.Y + nnset,
                    System.Math.Max(0, rect.Width - BorderThickness),
                    System.Math.Max(0, rect.Height - BorderThickness)),
                CornerRadius,
                CornerRadius,
                border,
                BorderThickness);
        }
        if (AccentBar is { } bar)
        {
            float accentBarThickness = GetAccentBarThickness();
            double barX = FlowDirection == FlowDirection.RightToLeft
                ? Bounds.X + Bounds.Width - Margin.Right - accentBarThickness
                : Bounds.X + Margin.Left;
            var rect = new Rect(barX, Bounds.Y + Margin.Top,
                                accentBarThickness, GetChromeHeight());
            if (accentBarThickness > 0)
                ds.FillRectangle(rect, bar);
        }
        int first = VerticalViewportIndex.FindFirstIntersecting(_childBottomEdges, viewport.Top);
        for (int index = first; index < _children.Count; index++)
        {
            BlockBox c = _children[index];
            if (c.Bounds.Top > viewport.Bottom) break;
            if (c.Bounds.Bottom < viewport.Top || c.Bounds.Top > viewport.Bottom) continue;
            c.Paint(ds, viewport);
        }
    }

    public override void PaintSelectionForeground(
        CanvasDrawingSession ds,
        DocumentRange range,
        Windows.UI.Color color,
        Rect viewport)
    {
        int first = VerticalViewportIndex.FindFirstIntersecting(_childBottomEdges, viewport.Top);
        for (int index = first; index < _children.Count; index++)
        {
            BlockBox c = _children[index];
            if (c.Bounds.Top > viewport.Bottom) break;
            if (c.Bounds.Bottom < viewport.Top || c.Bounds.Top > viewport.Bottom) continue;
            c.PaintSelectionForeground(ds, range, color, viewport);
        }
    }

    internal (int Start, int End) GetChildRangeInVerticalBand(double top, double bottom)
    {
        int first = VerticalViewportIndex.FindFirstIntersecting(_childBottomEdges, top);
        int end = first;
        while (end < _children.Count && _children[end].Bounds.Top <= bottom)
            end++;
        return (first, end);
    }

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        int first = VerticalViewportIndex.FindFirstIntersecting(_childBottomEdges, point.Y);
        for (int index = first; index < _children.Count; index++)
        {
            BlockBox c = _children[index];
            if (c.Bounds.Top > point.Y) break;
            if (c.HitTest(point, out position)) return true;
        }
        // Padding-area hits return false — the StackBox itself has no source-map
        // entry, so reporting (BlockIndex, 0, 0) for these would produce
        // incorrect copy ranges in MarkdownSourceMap.Slice.
        position = new DocumentPosition(BlockIndex, 0, 0);
        return false;
    }

    internal override bool HitTestSelectionEndpoint(Point point, out DocumentPosition position)
    {
        int first = VerticalViewportIndex.FindFirstIntersecting(_childBottomEdges, point.Y);
        for (int index = first; index < _children.Count; index++)
        {
            BlockBox child = _children[index];
            if (child.Bounds.Top > point.Y) break;
            if (child.HitTestSelectionEndpoint(point, out position)) return true;
        }

        position = new DocumentPosition(BlockIndex, 0, 0);
        return false;
    }

    public override void Dispose()
    {
        var cleanup = new BlockBoxCleanupScope();
        foreach (BlockBox child in _children)
            cleanup.TryDispose(child);
        cleanup.ThrowIfAny("disposing a stack box");
    }

    internal override void CollectRetirementTasks(List<Task> pendingTasks)
    {
        var cleanup = new BlockBoxCleanupScope();
        foreach (BlockBox child in _children)
            cleanup.TryCollectRetirementTasks(child, pendingTasks);
        cleanup.ThrowIfAny("collecting stack-box retirement tasks");
    }

    private void RefreshChildBottomEdges()
    {
        if (_childBottomEdges.Length != _children.Count)
            _childBottomEdges = new double[_children.Count];
        for (int index = 0; index < _children.Count; index++)
            _childBottomEdges[index] = _children[index].Bounds.Bottom;
    }

    internal static double GetPositiveCollapsedMargin(double previousBottom, double currentTop)
    {
        if (!double.IsFinite(previousBottom) || !double.IsFinite(currentTop) ||
            previousBottom < 0 || currentTop < 0)
        {
            return 0;
        }

        return Math.Min(previousBottom, currentTop);
    }

    private float GetAccentBarContentInset()
        => AccentBar is not null && AccentBarAffectsContentLayout
            ? GetAccentBarThickness()
            : 0;

    private float GetAccentBarThickness()
        => float.IsFinite(AccentBarThickness)
            ? Math.Max(0, AccentBarThickness)
            : 3f;

    private double GetChromeHeight()
        => Math.Max(
            0,
            Bounds.Height - Margin.Top - Margin.Bottom - _trailingChildMarginOutsideChrome);
}
