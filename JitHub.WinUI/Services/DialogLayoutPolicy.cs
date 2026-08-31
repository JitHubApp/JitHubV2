using System;

namespace JitHub.Services;

internal enum AppDialogLayoutKind
{
    Confirmation,
    CompactForm,
    Standard,
    Editor
}

internal readonly record struct DialogLayoutMetrics(
    double OuterMargin,
    double MinimumWidth,
    double MaximumWidth,
    double MaximumHeight);

internal readonly record struct DialogLayoutTokenSet(
    double CompactBreakpoint,
    double CompactMargin,
    double StandardMargin,
    double ConfirmationPreferredWidth,
    double CompactFormPreferredWidth,
    double StandardPreferredWidth,
    double EditorPreferredWidth,
    double ConfirmationPreferredHeight,
    double CompactFormPreferredHeight,
    double StandardPreferredHeight,
    double EditorPreferredHeight,
    double PreferredMinimumWidth);

internal static class DialogLayoutPolicy
{
    public static DialogLayoutMetrics Calculate(
        double viewportWidth,
        double viewportHeight,
        DialogLayoutTokenSet tokens,
        AppDialogLayoutKind kind = AppDialogLayoutKind.Standard)
    {
        double safeWidth = NormalizeViewportDimension(
            viewportWidth,
            tokens.EditorPreferredWidth + (tokens.StandardMargin * 2));
        double safeHeight = NormalizeViewportDimension(
            viewportHeight,
            tokens.EditorPreferredHeight + (tokens.StandardMargin * 2));
        double margin = safeWidth < tokens.CompactBreakpoint
            ? tokens.CompactMargin
            : tokens.StandardMargin;
        double availableWidth = Math.Max(0, safeWidth - (margin * 2));
        double availableHeight = Math.Max(0, safeHeight - (margin * 2));
        (double preferredWidth, double preferredHeight) = kind switch
        {
            AppDialogLayoutKind.Confirmation =>
                (tokens.ConfirmationPreferredWidth, tokens.ConfirmationPreferredHeight),
            AppDialogLayoutKind.CompactForm =>
                (tokens.CompactFormPreferredWidth, tokens.CompactFormPreferredHeight),
            AppDialogLayoutKind.Editor =>
                (tokens.EditorPreferredWidth, tokens.EditorPreferredHeight),
            _ => (tokens.StandardPreferredWidth, tokens.StandardPreferredHeight)
        };
        double maximumWidth = Math.Min(preferredWidth, availableWidth);

        return new DialogLayoutMetrics(
            margin,
            Math.Min(tokens.PreferredMinimumWidth, maximumWidth),
            maximumWidth,
            Math.Min(preferredHeight, availableHeight));
    }

    private static double NormalizeViewportDimension(double value, double fallback) =>
        double.IsFinite(value) && value > 0 ? value : fallback;
}
