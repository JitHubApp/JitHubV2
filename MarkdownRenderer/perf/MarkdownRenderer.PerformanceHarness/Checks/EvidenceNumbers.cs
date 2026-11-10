namespace MarkdownRenderer.PerformanceHarness.Checks;

/// <summary>
/// Numeric predicates shared by the harness's report self-checks.
/// </summary>
internal static class EvidenceNumbers
{
    internal static bool IsFinitePositive(double value)
        => double.IsFinite(value) && value > 0;

    internal static bool IsFiniteNonNegative(double value)
        => double.IsFinite(value) && value >= 0;

    /// <summary>
    /// True when both values are finite and agree to a relative tolerance of
    /// 1e-12 (absolute below magnitude one).
    /// </summary>
    internal static bool NearlyEqual(double left, double right)
    {
        if (!double.IsFinite(left) || !double.IsFinite(right))
            return false;
        double scale = Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= scale * 1e-12;
    }

    internal static bool IsSha256(string? value)
        => value is { Length: 64 } &&
           value.All(static character =>
               character is >= '0' and <= '9' or
               >= 'a' and <= 'f' or
               >= 'A' and <= 'F');
}
