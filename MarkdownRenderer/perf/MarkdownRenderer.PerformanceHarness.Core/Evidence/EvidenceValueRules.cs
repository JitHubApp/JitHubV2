using System.Globalization;

namespace MarkdownRenderer.PerformanceHarness.Core.Evidence;

/// <summary>
/// Value checks that every evidence validator shares: hash and adapter
/// identifier shapes, finite non-negative numbers, and the relative
/// tolerance used when a recomputed statistic is compared with the
/// serialized one.
/// </summary>
internal static class EvidenceValueRules
{
    internal static bool IsGpuAdapterLuid(string? value)
    {
        if (value is null)
            return false;

        string[] parts = value.Split(':');
        return parts.Length == 2 &&
               parts.All(static part =>
                   part.Length == 8 &&
                   uint.TryParse(
                       part,
                       NumberStyles.AllowHexSpecifier,
                       CultureInfo.InvariantCulture,
                       out _));
    }

    internal static bool IsSha256(string? value)
        => value is { Length: 64 } &&
           value.All(static character =>
               character is >= '0' and <= '9' or
               >= 'a' and <= 'f' or
               >= 'A' and <= 'F');

    internal static bool AllFiniteNonNegative(params double[] values)
        => values.All(IsFiniteNonNegative);

    internal static bool IsFinitePositive(double value)
        => double.IsFinite(value) && value > 0;

    internal static bool IsFiniteNonNegative(double value)
        => double.IsFinite(value) && value >= 0;

    internal static bool NearlyEqual(double left, double right)
    {
        if (!double.IsFinite(left) || !double.IsFinite(right))
            return false;
        double scale = Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= scale * 1e-12;
    }
}
