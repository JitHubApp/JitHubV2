namespace MarkdownRenderer.PerformanceHarness;

internal static class PerformanceStatistics
{
    /// <summary>
    /// Returns the conventional median. Even-sized populations use the
    /// midpoint of the two central values rather than a nearest-rank value.
    /// </summary>
    internal static double Median(IEnumerable<double>? values)
    {
        if (values is null)
            return double.NaN;

        double[] sorted = values.ToArray();
        if (sorted.Length == 0 || sorted.Any(static value => !double.IsFinite(value)))
            return double.NaN;

        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return (sorted.Length & 1) != 0
            ? sorted[middle]
            : TwoValueCenter(sorted[middle - 1], sorted[middle]);
    }

    /// <summary>
    /// Returns the finite midpoint of two finite values without overflowing
    /// when their direct sum is outside the <see cref="double"/> range.
    /// </summary>
    internal static double TwoValueCenter(double first, double second)
    {
        if (!double.IsFinite(first) || !double.IsFinite(second))
            return double.NaN;

        bool sameSign = (first < 0) == (second < 0);
        return sameSign
            ? first + ((second - first) / 2)
            : (first + second) / 2;
    }

    internal static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            return double.NaN;

        int rank = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[rank];
    }

    internal static double Percentile(IEnumerable<long> values, double percentile)
        => Percentile(values.Select(static value => (double)value), percentile);

    internal static double DeltaPercent(double candidate, double reference)
    {
        if (reference == 0)
            return candidate == 0 ? 0 : double.MaxValue;
        return ((candidate / reference) - 1) * 100;
    }

    /// <summary>
    /// Returns the one-sample Hodges-Lehmann location estimate: the median of
    /// every Walsh average (x[i] + x[j]) / 2 where i is less than or equal to j.
    /// </summary>
    internal static double HodgesLehmann(IEnumerable<double>? values)
    {
        if (values is null)
            return double.NaN;

        double[] samples = values.ToArray();
        if (samples.Length == 0 || samples.Any(static value => !double.IsFinite(value)))
            return double.NaN;

        long walshAverageCount = (long)samples.Length * (samples.Length + 1L) / 2;
        if (walshAverageCount > Array.MaxLength)
            return double.NaN;

        var walshAverages = new double[(int)walshAverageCount];
        int next = 0;
        for (int left = 0; left < samples.Length; left++)
        {
            for (int right = left; right < samples.Length; right++)
                walshAverages[next++] = TwoValueCenter(samples[left], samples[right]);
        }

        return Median(walshAverages);
    }

    /// <summary>
    /// Returns a robust, scale-independent dispersion percentage using the
    /// consistency-scaled median absolute deviation around the sample median.
    /// </summary>
    internal static double RobustDispersionPercent(IEnumerable<double>? values)
    {
        if (values is null)
            return double.NaN;

        double[] samples = values.ToArray();
        double location = HodgesLehmann(samples);
        if (!double.IsFinite(location))
            return double.NaN;

        double median = Median(samples);
        double mad = Median(samples.Select(value => Math.Abs(value - median)));
        if (!double.IsFinite(mad))
            return double.NaN;
        if (Math.Abs(location) <= 1e-12)
            return mad <= 1e-12 ? 0 : double.MaxValue;

        return 1.4826 * mad / Math.Abs(location) * 100;
    }

    /// <summary>
    /// Returns the Theil-Sen slope of finite observations with distinct,
    /// strictly increasing, non-negative global ordinals. Ordinals may contain
    /// gaps. Invalid populations return <see cref="double.NaN"/>.
    /// </summary>
    internal static double TheilSenSlope(
        IEnumerable<(int GlobalOrdinal, double Value)>? observations)
        => TryCalculateTheilSen(observations, out double slope, out _)
            ? slope
            : double.NaN;

    /// <summary>
    /// Projects the signed Theil-Sen slope across the full observed ordinal
    /// span. Invalid populations or non-finite projections return
    /// <see cref="double.NaN"/>.
    /// </summary>
    internal static double ProjectedTheilSenDrift(
        IEnumerable<(int GlobalOrdinal, double Value)>? observations)
    {
        if (!TryCalculateTheilSen(observations, out double slope, out int ordinalSpan))
            return double.NaN;

        double projectedDrift = slope * ordinalSpan;
        return double.IsFinite(projectedDrift) ? projectedDrift : double.NaN;
    }

    private static bool TryCalculateTheilSen(
        IEnumerable<(int GlobalOrdinal, double Value)>? observations,
        out double slope,
        out int ordinalSpan)
    {
        slope = double.NaN;
        ordinalSpan = 0;
        if (observations is null)
            return false;

        (int GlobalOrdinal, double Value)[] ordered = observations.ToArray();
        if (ordered.Length < 2 || ordered.Any(static observation =>
                observation.GlobalOrdinal < 0 || !double.IsFinite(observation.Value)))
        {
            return false;
        }

        for (int index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].GlobalOrdinal <= ordered[index - 1].GlobalOrdinal)
                return false;
        }

        ordinalSpan = ordered[^1].GlobalOrdinal - ordered[0].GlobalOrdinal;
        if (ordinalSpan <= 0)
            return false;

        long pairCount = (long)ordered.Length * (ordered.Length - 1L) / 2;
        if (pairCount > Array.MaxLength)
            return false;

        var pairSlopes = new double[(int)pairCount];
        int next = 0;
        for (int left = 0; left < ordered.Length; left++)
        {
            for (int right = left + 1; right < ordered.Length; right++)
            {
                long ordinalDelta =
                    (long)ordered[right].GlobalOrdinal - ordered[left].GlobalOrdinal;
                double valueDelta = ordered[right].Value - ordered[left].Value;
                double pairSlope = valueDelta / ordinalDelta;
                if (!double.IsFinite(pairSlope))
                    return false;

                pairSlopes[next++] = pairSlope;
            }
        }

        slope = Median(pairSlopes);
        return double.IsFinite(slope);
    }

    internal static double GrowthPercent(long final, long baseline)
    {
        if (baseline <= 0)
            return final <= baseline ? 0 : double.MaxValue;
        return Math.Max(0, (final - baseline) * 100.0 / baseline);
    }
}
