namespace MarkdownRenderer.ScrollFidelityProbe.Capture;

/// <summary>
/// One presented frame of the measured viewport: when it arrived, its height
/// in physical pixels, and every run of uniform rows at least
/// <see cref="ViewportRowAnalyzer.MinimumRecordedRun"/> rows long.
/// </summary>
internal sealed record FrameSample(long Timestamp, int Height, UniformRun[] Runs)
{
    /// <summary>Rows inside uniform runs at least <paramref name="threshold"/> rows long.</summary>
    internal int BlankRows(int threshold)
    {
        int rows = 0;
        foreach (UniformRun run in Runs)
        {
            if (run.Length >= threshold)
                rows += run.Length;
        }

        return rows;
    }

    internal double BlankFraction(int threshold) => Height <= 0 ? 0 : BlankRows(threshold) / (double)Height;

    internal int LongestRun => Runs.Length == 0 ? 0 : Runs.Max(run => run.Length);
}
