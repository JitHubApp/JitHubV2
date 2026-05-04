using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace CommunityToolkit.WinUI.UI;

public static class VisualExtensions
{
    public static readonly DependencyProperty NormalizedCenterPointProperty =
        DependencyProperty.RegisterAttached(
            "NormalizedCenterPoint",
            typeof(double),
            typeof(VisualExtensions),
            new PropertyMetadata(double.NaN, OnNormalizedCenterPointChanged));

    private static readonly DependencyProperty IsSubscribedProperty =
        DependencyProperty.RegisterAttached(
            "IsSubscribed",
            typeof(bool),
            typeof(VisualExtensions),
            new PropertyMetadata(false));

    public static double GetNormalizedCenterPoint(DependencyObject obj)
        => (double)obj.GetValue(NormalizedCenterPointProperty);

    public static void SetNormalizedCenterPoint(DependencyObject obj, double value)
        => obj.SetValue(NormalizedCenterPointProperty, value);

    private static void OnNormalizedCenterPointChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        EnsureSubscribed(element);
        UpdateCenterPoint(element);
    }

    private static void EnsureSubscribed(FrameworkElement element)
    {
        if ((bool)element.GetValue(IsSubscribedProperty))
        {
            return;
        }

        element.Loaded += OnElementLoaded;
        element.SizeChanged += OnElementSizeChanged;
        element.SetValue(IsSubscribedProperty, true);
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
        => UpdateCenterPoint((FrameworkElement)sender);

    private static void OnElementSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateCenterPoint((FrameworkElement)sender);

    private static void UpdateCenterPoint(FrameworkElement element)
    {
        double normalizedCenterPoint = GetNormalizedCenterPoint(element);
        if (double.IsNaN(normalizedCenterPoint))
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3(
            (float)(element.ActualWidth * normalizedCenterPoint),
            (float)(element.ActualHeight * normalizedCenterPoint),
            0f);
    }
}
