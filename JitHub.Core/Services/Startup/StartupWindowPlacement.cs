using System;
using JitHub.Core.Models.Startup;

namespace JitHub.Core.Services.Startup;

/// <summary>
/// Computes the first-launch window rectangle. The splash and the main window
/// share it so the handoff between them does not move or resize anything.
/// </summary>
public static class StartupWindowPlacement
{
    private const int DefaultWidthDip = 1360;
    private const int DefaultHeightDip = 900;
    private const int MinimumWidthDip = 960;
    private const int MinimumHeightDip = 640;
    private const double MaximumWorkAreaFraction = 0.88;
    private const double BaseDpi = 96d;

    public static StartupWindowBounds Calculate(StartupWindowBounds workArea, uint dpi)
    {
        double scale = Math.Max(1d, dpi / BaseDpi);
        int maxWidth = Math.Max(1, (int)Math.Round(workArea.Width * MaximumWorkAreaFraction));
        int maxHeight = Math.Max(1, (int)Math.Round(workArea.Height * MaximumWorkAreaFraction));
        int width = Math.Clamp(
            DipToPixels(DefaultWidthDip, scale),
            Math.Min(DipToPixels(MinimumWidthDip, scale), maxWidth),
            maxWidth);
        int height = Math.Clamp(
            DipToPixels(DefaultHeightDip, scale),
            Math.Min(DipToPixels(MinimumHeightDip, scale), maxHeight),
            maxHeight);

        return new StartupWindowBounds(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2),
            width,
            height);
    }

    private static int DipToPixels(int dips, double scale) =>
        Math.Max(1, (int)Math.Round(dips * scale));
}
