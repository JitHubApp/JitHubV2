using System;

namespace JitHub.Services.Layout;

public readonly record struct ResponsiveUniformGridMetrics(int Columns, double ItemWidth);

public static class ResponsiveUniformGridLayoutPolicy
{
    public static ResponsiveUniformGridMetrics Calculate(
        double availableWidth,
        int itemCount,
        double minimumItemWidth,
        double spacing)
    {
        if (itemCount <= 0)
        {
            return new ResponsiveUniformGridMetrics(0, 0);
        }

        double safeMinimumWidth = double.IsFinite(minimumItemWidth)
            ? Math.Max(1, minimumItemWidth)
            : 1;
        double safeSpacing = double.IsFinite(spacing)
            ? Math.Max(0, spacing)
            : 0;

        if (double.IsPositiveInfinity(availableWidth))
        {
            return new ResponsiveUniformGridMetrics(itemCount, safeMinimumWidth);
        }

        double safeWidth = double.IsFinite(availableWidth)
            ? Math.Max(0, availableWidth)
            : 0;
        double candidateColumns = Math.Floor(
            (safeWidth + safeSpacing) / (safeMinimumWidth + safeSpacing));
        int columns = candidateColumns >= itemCount
            ? itemCount
            : candidateColumns >= 1
                ? (int)candidateColumns
                : 1;
        double itemWidth = Math.Max(
            0,
            (safeWidth - (safeSpacing * (columns - 1))) / columns);

        return new ResponsiveUniformGridMetrics(columns, itemWidth);
    }
}
