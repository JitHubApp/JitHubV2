using System;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Layout.Internal.Boxes;

/// <summary>
/// The layout-affecting settings of a source StackBox captured for a block
/// measured-placement transfer, compared again at the final gate.
/// </summary>
internal readonly struct BlockMeasuredPlacementStackSettings(
    Thickness ContentPadding,
    Windows.UI.Color? AccentBar,
    float AccentBarThickness,
    bool AccentBarAffectsContentLayout,
    Windows.UI.Color? Background,
    Windows.UI.Color? BorderBrush,
    float BorderThickness,
    float CornerRadius,
    float InterChildSpacing,
    bool CollapsePositiveAdjacentMargins,
    FlowDirection FlowDirection,
    bool HasResolvedSafeHtmlAlignment)
{
    public Thickness ContentPadding { get; init; } = ContentPadding;
    public Windows.UI.Color? AccentBar { get; init; } = AccentBar;
    public float AccentBarThickness { get; init; } = AccentBarThickness;
    public bool AccentBarAffectsContentLayout { get; init; } = AccentBarAffectsContentLayout;
    public Windows.UI.Color? Background { get; init; } = Background;
    public Windows.UI.Color? BorderBrush { get; init; } = BorderBrush;
    public float BorderThickness { get; init; } = BorderThickness;
    public float CornerRadius { get; init; } = CornerRadius;
    public float InterChildSpacing { get; init; } = InterChildSpacing;
    public bool CollapsePositiveAdjacentMargins { get; init; } = CollapsePositiveAdjacentMargins;
    public FlowDirection FlowDirection { get; init; } = FlowDirection;
    public bool HasResolvedSafeHtmlAlignment { get; init; } = HasResolvedSafeHtmlAlignment;

    internal static BlockMeasuredPlacementStackSettings Capture(StackBox stack) => new(
        stack.ContentPadding,
        stack.AccentBar,
        stack.AccentBarThickness,
        stack.AccentBarAffectsContentLayout,
        stack.Background,
        stack.BorderBrush,
        stack.BorderThickness,
        stack.CornerRadius,
        stack.InterChildSpacing,
        stack.CollapsePositiveAdjacentMargins,
        stack.FlowDirection,
        stack.HasResolvedSafeHtmlAlignment);

    internal bool Matches(StackBox stack) =>
        stack.ContentPadding.Equals(ContentPadding) &&
        Nullable.Equals(stack.AccentBar, AccentBar) &&
        stack.AccentBarThickness == AccentBarThickness &&
        stack.AccentBarAffectsContentLayout == AccentBarAffectsContentLayout &&
        Nullable.Equals(stack.Background, Background) &&
        Nullable.Equals(stack.BorderBrush, BorderBrush) &&
        stack.BorderThickness == BorderThickness &&
        stack.CornerRadius == CornerRadius &&
        stack.InterChildSpacing == InterChildSpacing &&
        stack.CollapsePositiveAdjacentMargins == CollapsePositiveAdjacentMargins &&
        stack.FlowDirection == FlowDirection &&
        stack.HasResolvedSafeHtmlAlignment == HasResolvedSafeHtmlAlignment;
}
