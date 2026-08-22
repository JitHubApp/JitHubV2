using System;

namespace JitHub.Services;

internal enum AppDialogLayoutKind
{
    Standard,
    Editor
}

internal readonly record struct DialogLayoutMetrics(
    double OuterMargin,
    double MinimumWidth,
    double MaximumWidth,
    double MaximumHeight);

internal static class DialogLayoutPolicy
{
    private const double CompactBreakpoint = 640;
    private const double CompactMargin = 12;
    private const double StandardMargin = 24;
    private const double StandardPreferredWidth = 620;
    private const double EditorPreferredWidth = 840;
    private const double StandardPreferredHeight = 520;
    private const double EditorPreferredHeight = 720;
    private const double PreferredMinimumWidth = 320;

    public static DialogLayoutMetrics Calculate(
        double viewportWidth,
        double viewportHeight,
        AppDialogLayoutKind kind = AppDialogLayoutKind.Standard)
    {
        double safeWidth = NormalizeViewportDimension(viewportWidth, EditorPreferredWidth + (StandardMargin * 2));
        double safeHeight = NormalizeViewportDimension(viewportHeight, EditorPreferredHeight + (StandardMargin * 2));
        double margin = safeWidth < CompactBreakpoint ? CompactMargin : StandardMargin;
        double availableWidth = Math.Max(0, safeWidth - (margin * 2));
        double availableHeight = Math.Max(0, safeHeight - (margin * 2));
        double preferredWidth = kind == AppDialogLayoutKind.Editor
            ? EditorPreferredWidth
            : StandardPreferredWidth;
        double preferredHeight = kind == AppDialogLayoutKind.Editor
            ? EditorPreferredHeight
            : StandardPreferredHeight;
        double maximumWidth = Math.Min(preferredWidth, availableWidth);

        return new DialogLayoutMetrics(
            margin,
            Math.Min(PreferredMinimumWidth, maximumWidth),
            maximumWidth,
            Math.Min(preferredHeight, availableHeight));
    }

    private static double NormalizeViewportDimension(double value, double fallback) =>
        double.IsFinite(value) && value > 0 ? value : fallback;
}
