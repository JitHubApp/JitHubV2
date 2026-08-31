using System;
using JitHub.Services.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace JitHub.WinUI.Views.Controls.App;

public sealed partial class AppResponsiveUniformGridLayout : VirtualizingLayout
{
    public static readonly DependencyProperty MinimumItemWidthProperty = DependencyProperty.Register(
        nameof(MinimumItemWidth),
        typeof(double),
        typeof(AppResponsiveUniformGridLayout),
        new PropertyMetadata(1d));

    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing),
        typeof(double),
        typeof(AppResponsiveUniformGridLayout),
        new PropertyMetadata(0d));

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing),
        typeof(double),
        typeof(AppResponsiveUniformGridLayout),
        new PropertyMetadata(0d));

    public double MinimumItemWidth
    {
        get => (double)GetValue(MinimumItemWidthProperty);
        set => SetValue(MinimumItemWidthProperty, value);
    }

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        ResponsiveUniformGridMetrics metrics = ResponsiveUniformGridLayoutPolicy.Calculate(
            availableSize.Width,
            context.ItemCount,
            MinimumItemWidth,
            HorizontalSpacing);
        if (metrics.Columns == 0)
        {
            return new Size(NormalizeFinite(availableSize.Width), 0);
        }

        double totalHeight = 0;
        for (int rowStart = 0; rowStart < context.ItemCount; rowStart += metrics.Columns)
        {
            double rowHeight = 0;
            int rowEnd = Math.Min(context.ItemCount, rowStart + metrics.Columns);
            for (int index = rowStart; index < rowEnd; index++)
            {
                UIElement element = context.GetOrCreateElementAt(index);
                element.Measure(new Size(metrics.ItemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, element.DesiredSize.Height);
            }

            totalHeight += rowHeight;
            if (rowEnd < context.ItemCount)
            {
                totalHeight += Math.Max(0, VerticalSpacing);
            }
        }

        double desiredWidth = double.IsPositiveInfinity(availableSize.Width)
            ? (metrics.Columns * metrics.ItemWidth) +
                (Math.Max(0, HorizontalSpacing) * (metrics.Columns - 1))
            : NormalizeFinite(availableSize.Width);
        return new Size(desiredWidth, totalHeight);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        ResponsiveUniformGridMetrics metrics = ResponsiveUniformGridLayoutPolicy.Calculate(
            finalSize.Width,
            context.ItemCount,
            MinimumItemWidth,
            HorizontalSpacing);
        if (metrics.Columns == 0)
        {
            return new Size(NormalizeFinite(finalSize.Width), 0);
        }

        double horizontalSpacing = Math.Max(0, HorizontalSpacing);
        double verticalSpacing = Math.Max(0, VerticalSpacing);
        double y = 0;
        for (int rowStart = 0; rowStart < context.ItemCount; rowStart += metrics.Columns)
        {
            int rowEnd = Math.Min(context.ItemCount, rowStart + metrics.Columns);
            double rowHeight = 0;
            for (int index = rowStart; index < rowEnd; index++)
            {
                rowHeight = Math.Max(
                    rowHeight,
                    context.GetOrCreateElementAt(index).DesiredSize.Height);
            }

            for (int index = rowStart; index < rowEnd; index++)
            {
                int column = index - rowStart;
                context.GetOrCreateElementAt(index).Arrange(new Rect(
                    column * (metrics.ItemWidth + horizontalSpacing),
                    y,
                    metrics.ItemWidth,
                    rowHeight));
            }

            y += rowHeight;
            if (rowEnd < context.ItemCount)
            {
                y += verticalSpacing;
            }
        }

        return new Size(NormalizeFinite(finalSize.Width), y);
    }

    private static double NormalizeFinite(double value) =>
        double.IsFinite(value) ? Math.Max(0, value) : 0;
}
