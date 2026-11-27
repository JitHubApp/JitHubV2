namespace MarkdownRenderer.ScrollFidelityProbe.Capture;

/// <summary>
/// Finds runs of uniform rows in a BGRA viewport sample. Rendered markdown
/// has short uniform runs between lines and blocks; an unrendered region is
/// one long uniform band. The report separates the two with a threshold
/// calibrated from settled frames of the same document.
/// </summary>
internal static class ViewportRowAnalyzer
{
    internal const int MinimumRecordedRun = 4;
    private const int ChannelTolerance = 6;

    internal static UniformRun[] Analyze(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var runs = new List<UniformRun>();
        int runStart = -1;
        for (int y = 0; y < height; y++)
        {
            bool uniform = IsUniformRow(bgra.Slice(y * width * 4, width * 4));
            if (uniform && runStart < 0)
            {
                runStart = y;
            }
            else if (!uniform && runStart >= 0)
            {
                AddRun(runs, runStart, y - runStart);
                runStart = -1;
            }
        }

        if (runStart >= 0)
            AddRun(runs, runStart, height - runStart);
        return [.. runs];
    }

    private static void AddRun(List<UniformRun> runs, int start, int length)
    {
        if (length >= MinimumRecordedRun)
            runs.Add(new UniformRun(start, length));
    }

    private static bool IsUniformRow(ReadOnlySpan<byte> row)
    {
        byte b = row[0], g = row[1], r = row[2];
        for (int offset = 4; offset < row.Length; offset += 4)
        {
            if (Math.Abs(row[offset] - b) > ChannelTolerance ||
                Math.Abs(row[offset + 1] - g) > ChannelTolerance ||
                Math.Abs(row[offset + 2] - r) > ChannelTolerance)
            {
                return false;
            }
        }

        return true;
    }
}
