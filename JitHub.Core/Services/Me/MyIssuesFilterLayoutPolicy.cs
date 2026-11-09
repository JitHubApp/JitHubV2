using System;
using System.Collections.Generic;
using System.Linq;

namespace JitHub.Services;

public static class MyIssuesFilterLayoutPolicy
{
    private const double EstimatedGlyphWidth = 7.5;
    private const double ItemHorizontalChrome = 16;
    private const double MinimumItemWidth = 52;

    public static bool ShouldUseCompact(
        double availableWidth,
        IReadOnlyList<string> scopeLabels,
        IReadOnlyList<string> stateLabels)
    {
        ArgumentNullException.ThrowIfNull(scopeLabels);
        ArgumentNullException.ThrowIfNull(stateLabels);
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return false;
        }

        double requiredWidth = Math.Max(
            EstimateSegmentedWidth(scopeLabels),
            EstimateSegmentedWidth(stateLabels));
        return availableWidth < requiredWidth;
    }

    public static double EstimateSegmentedWidth(IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        return labels.Sum(label => Math.Max(
            MinimumItemWidth,
            (label?.Length ?? 0) * EstimatedGlyphWidth + ItemHorizontalChrome));
    }
}
