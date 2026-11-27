using System.Diagnostics;
using MarkdownRenderer.ScrollFidelityProbe.Capture;
using MarkdownRenderer.ScrollFidelityProbe.Scenarios;

namespace MarkdownRenderer.ScrollFidelityProbe.Reports;

/// <summary>Turns a scenario's captured frames and UI trace into a <see cref="ScenarioResult"/>.</summary>
internal static class ScenarioResultBuilder
{
    /// <summary>Consecutive blank-free frames that count as a filled landing.</summary>
    private const int FilledFrames = 2;

    internal static ScenarioResult Build(
        IScrollScenario scenario,
        double firstRenderMs,
        IReadOnlyList<FrameSample> frames,
        ScrollRecorder recorder,
        int threshold)
    {
        double[] blank = frames.Select(frame => frame.BlankFraction(threshold) * 100).ToArray();
        long start = frames.Count > 0 ? frames[0].Timestamp : 0;
        long end = frames.Count > 0 ? frames[^1].Timestamp : 0;
        double blankSeconds = 0;
        for (int index = 1; index < frames.Count; index++)
        {
            double interval = Stopwatch.GetElapsedTime(frames[index - 1].Timestamp, frames[index].Timestamp).TotalSeconds;
            blankSeconds += interval * blank[index - 1] / 100;
        }

        double[] velocities = Velocities(recorder.Offsets);
        return new ScenarioResult
        {
            Name = scenario.Name,
            Description = scenario.Description,
            FirstRenderMs = firstRenderMs,
            Frames = frames.Count,
            DurationMs = Stopwatch.GetElapsedTime(start, end).TotalMilliseconds,
            BlankMeanPercent = blank.Length == 0 ? 0 : blank.Average(),
            BlankP50Percent = Percentile(blank, 0.50),
            BlankP95Percent = Percentile(blank, 0.95),
            BlankP99Percent = Percentile(blank, 0.99),
            BlankMaxPercent = blank.Length == 0 ? 0 : blank.Max(),
            FramesWithBlankPercent = blank.Length == 0 ? 0 : blank.Count(value => value > 0) * 100.0 / blank.Length,
            BlankViewportSeconds = blankSeconds,
            ScrollVelocityP50DipsPerSecond = Percentile(velocities, 0.50),
            ScrollVelocityP95DipsPerSecond = Percentile(velocities, 0.95),
            UiTickP50Ms = Percentile(recorder.UiTickIntervalsMs, 0.50),
            UiTickP95Ms = Percentile(recorder.UiTickIntervalsMs, 0.95),
            UiTickP99Ms = Percentile(recorder.UiTickIntervalsMs, 0.99),
            UiTickMaxMs = recorder.UiTickIntervalsMs.DefaultIfEmpty(0).Max(),
            UiTicksOver33Ms = recorder.UiTickIntervalsMs.Count(value => value > 33.4),
            JumpFillMs = FillLatencies(frames, blank, recorder.JumpLandings),
            Series = frames.Select((frame, index) => new[]
            {
                Math.Round(Stopwatch.GetElapsedTime(start, frame.Timestamp).TotalMilliseconds, 1),
                Math.Round(blank[index], 2),
            }).ToList(),
        };
    }

    internal static double Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0;
        double[] sorted = values.Order().ToArray();
        int rank = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    private static double[] Velocities(List<(long Timestamp, double Offset)> offsets)
    {
        var velocities = new List<double>();
        for (int index = 1; index < offsets.Count; index++)
        {
            double seconds = Stopwatch.GetElapsedTime(offsets[index - 1].Timestamp, offsets[index].Timestamp).TotalSeconds;
            if (seconds > 0)
                velocities.Add(Math.Abs(offsets[index].Offset - offsets[index - 1].Offset) / seconds);
        }

        return velocities.Where(velocity => velocity > 1).ToArray();
    }

    /// <summary>
    /// For each landing, the time until the viewport shows no blank band for
    /// <see cref="FilledFrames"/> consecutive frames, or until the next landing.
    /// </summary>
    private static List<double> FillLatencies(IReadOnlyList<FrameSample> frames, double[] blank, List<long> landings)
    {
        var latencies = new List<double>();
        for (int landing = 0; landing < landings.Count; landing++)
        {
            long from = landings[landing];
            long until = landing + 1 < landings.Count ? landings[landing + 1] : long.MaxValue;
            int clean = 0;
            double? filled = null;
            for (int index = 0; index < frames.Count; index++)
            {
                FrameSample frame = frames[index];
                if (frame.Timestamp <= from)
                    continue;
                if (frame.Timestamp >= until)
                    break;
                clean = blank[index] > 0 ? 0 : clean + 1;
                if (clean == FilledFrames)
                {
                    // The first of the clean frames is when the viewport filled.
                    filled = Stopwatch.GetElapsedTime(from, frames[index - FilledFrames + 1].Timestamp).TotalMilliseconds;
                    break;
                }
            }

            latencies.Add(Math.Round(filled ?? Stopwatch.GetElapsedTime(from, Math.Min(until, frames.Count > 0 ? frames[^1].Timestamp : from)).TotalMilliseconds, 1));
        }

        return latencies;
    }
}
