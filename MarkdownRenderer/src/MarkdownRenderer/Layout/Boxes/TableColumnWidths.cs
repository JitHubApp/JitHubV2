using System;
using System.Threading;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Resolves a table's column widths: measures each column's padded minimum
/// and preferred intrinsic widths across header and body rows, then fits
/// them to the available viewport by growing preferred widths in proportion
/// or shrinking toward the minimums by each column's flexibility.
/// </summary>
internal static class TableColumnWidths
{
    private const float MinimumColumnWidth = 48f;
    private const float MaximumPreferredColumnWidth = 720f;

    internal static (float[] Minimum, float[] Preferred) MeasureIntrinsic(
        InlineContainerBox[][] headerCells,
        InlineContainerBox[][] bodyCells,
        int columnCount,
        Thickness headerPadding,
        Thickness bodyPadding,
        CancellationToken cancellationToken)
    {
        var minimum = new float[columnCount];
        var preferred = new float[columnCount];
        for (int c = 0; c < columnCount; c++)
        {
            minimum[c] = MinimumColumnWidth;
            preferred[c] = MinimumColumnWidth;
        }

        MeasureIntrinsicRows(headerCells, columnCount, headerPadding, minimum, preferred, cancellationToken);
        MeasureIntrinsicRows(bodyCells, columnCount, bodyPadding, minimum, preferred, cancellationToken);
        return (minimum, preferred);
    }

    private static void MeasureIntrinsicRows(
        InlineContainerBox[][] rows,
        int columnCount,
        Thickness padding,
        float[] minimum,
        float[] preferred,
        CancellationToken cancellationToken)
    {
        float horizontalPadding = (float)(padding.Left + padding.Right);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(row.Length, columnCount);
            for (int c = 0; c < count; c++)
            {
                IntrinsicWidthMetrics metrics = row[c].MeasureIntrinsicWidths(MaximumPreferredColumnWidth);
                minimum[c] = Math.Max(minimum[c], Math.Min(MaximumPreferredColumnWidth, metrics.Minimum + horizontalPadding));
                preferred[c] = Math.Max(preferred[c], Math.Min(MaximumPreferredColumnWidth, metrics.Preferred + horizontalPadding));
            }
        }
    }

    internal static float[] Resolve(
        float availableWidth,
        (float[] Minimum, float[] Preferred) intrinsic)
    {
        int count = intrinsic.Minimum.Length;
        var result = new float[count];
        float minimumTotal = Sum(intrinsic.Minimum);
        float preferredTotal = Sum(intrinsic.Preferred);

        if (preferredTotal <= availableWidth)
        {
            float extra = availableWidth - preferredTotal;
            float weightTotal = Math.Max(1f, preferredTotal);
            for (int c = 0; c < count; c++)
                result[c] = intrinsic.Preferred[c] + extra * intrinsic.Preferred[c] / weightTotal;
            return result;
        }

        if (minimumTotal >= availableWidth)
        {
            Array.Copy(intrinsic.Minimum, result, count);
            return result;
        }

        float distributable = availableWidth - minimumTotal;
        float flexibility = Math.Max(0.001f, preferredTotal - minimumTotal);
        for (int c = 0; c < count; c++)
        {
            float columnFlex = Math.Max(0, intrinsic.Preferred[c] - intrinsic.Minimum[c]);
            result[c] = intrinsic.Minimum[c] + distributable * columnFlex / flexibility;
        }
        return result;
    }

    internal static float Sum(float[] values)
    {
        float sum = 0;
        for (int i = 0; i < values.Length; i++)
            sum += values[i];
        return sum;
    }
}
