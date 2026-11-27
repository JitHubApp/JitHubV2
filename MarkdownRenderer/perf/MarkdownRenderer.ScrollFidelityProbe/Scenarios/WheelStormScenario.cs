using System.Diagnostics;
using MarkdownRenderer.ScrollFidelityProbe.Input;

namespace MarkdownRenderer.ScrollFidelityProbe.Scenarios;

/// <summary>
/// Real mouse-wheel input at free-spinning-wheel rate, injected with
/// SendInput over the renderer. The scroll viewer animates each notch on
/// the compositor, so this is the path a user's fast wheel takes.
/// </summary>
internal sealed class WheelStormScenario : IScrollScenario
{
    private const int NotchesPerSecond = 60;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(4);

    public string Name => "wheel";

    public string Description => $"{NotchesPerSecond} wheel notches/s for {Duration.TotalSeconds:0} s, injected with SendInput";

    public async Task RunAsync(ScenarioContext context, ScrollRecorder recorder)
    {
        NativeMethods.SetForegroundWindow(context.Window);
        NativeMethods.Point center = context.Host.GetScreenCenter(context.Window, context.RasterizationScale);
        NativeMethods.SetCursorPos(center.X, center.Y);
        await Task.Delay(100, context.Cancellation);

        CancellationToken cancellation = context.Cancellation;
        await Task.Run(() =>
        {
            long interval = Stopwatch.Frequency / NotchesPerSecond;
            long next = Stopwatch.GetTimestamp();
            long end = next + (long)(Duration.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < end && !cancellation.IsCancellationRequested)
            {
                NativeMethods.SendWheel(-NativeMethods.WheelDelta);
                next += interval;
                long wait = next - Stopwatch.GetTimestamp();
                if (wait > 0)
                    Thread.Sleep(TimeSpan.FromSeconds(wait / (double)Stopwatch.Frequency));
            }
        }, cancellation);
    }
}
