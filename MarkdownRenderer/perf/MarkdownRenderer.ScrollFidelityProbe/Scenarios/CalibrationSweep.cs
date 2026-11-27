using MarkdownRenderer.ScrollFidelityProbe.Capture;

namespace MarkdownRenderer.ScrollFidelityProbe.Scenarios;

/// <summary>
/// Visits evenly spaced offsets of a fresh renderer and lets each one settle
/// fully before reading the presented frame. The longest uniform band seen
/// in those settled frames is the document's legitimate spacing; anything
/// longer during a scenario is unrendered area.
/// </summary>
internal static class CalibrationSweep
{
    private const int Stops = 24;
    private const int SettleMilliseconds = 1200;

    internal static async Task<int> MeasureLongestSettledRunAsync(
        ScenarioContext context,
        WindowFrameCapture capture)
    {
        int longest = 0;
        for (int stop = 0; stop <= Stops; stop++)
        {
            double target = context.MaximumOffset * stop / Stops;
            context.ScrollViewer.ChangeView(null, target, null, disableAnimation: true);
            await Task.Delay(SettleMilliseconds, context.Cancellation);
            if (capture.LatestFrame is { } frame)
                longest = Math.Max(longest, frame.LongestRun);
        }

        return longest;
    }
}
