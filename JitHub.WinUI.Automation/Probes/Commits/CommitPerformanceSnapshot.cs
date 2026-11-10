namespace JitHub.WinUI.Automation.Probes.Commits;

internal readonly record struct CommitPerformanceSnapshot(
    double ElapsedMilliseconds,
    double FirstDiffMilliseconds,
    double SearchMilliseconds,
    double DispatcherMaxGapMilliseconds,
    double RenderMaxGapMilliseconds,
    int RenderCount)
{
    public static bool TryParse(string? value, out CommitPerformanceSnapshot snapshot)
    {
        snapshot = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        Dictionary<string, string> values = value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.Ordinal);

        return TryReadDouble(values, "elapsed_ms", out double elapsed) &&
            TryReadDouble(values, "first_diff_ms", out double firstDiff) &&
            TryReadDouble(values, "search_ms", out double search) &&
            TryReadDouble(values, "dispatcher_max_gap_ms", out double dispatcherGap) &&
            TryReadDouble(values, "render_max_gap_ms", out double renderGap) &&
            values.TryGetValue("render_count", out string? renderCountText) &&
            int.TryParse(renderCountText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int renderCount) &&
            SetSnapshot(
                elapsed,
                firstDiff,
                search,
                dispatcherGap,
                renderGap,
                renderCount,
                out snapshot);
    }

    private static bool TryReadDouble(
        IReadOnlyDictionary<string, string> values,
        string key,
        out double result)
    {
        result = default;
        return values.TryGetValue(key, out string? text) &&
            double.TryParse(
            text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out result);
    }

    private static bool SetSnapshot(
        double elapsed,
        double firstDiff,
        double search,
        double dispatcherGap,
        double renderGap,
        int renderCount,
        out CommitPerformanceSnapshot snapshot)
    {
        snapshot = new CommitPerformanceSnapshot(
            elapsed,
            firstDiff,
            search,
            dispatcherGap,
            renderGap,
            renderCount);
        return true;
    }
}
