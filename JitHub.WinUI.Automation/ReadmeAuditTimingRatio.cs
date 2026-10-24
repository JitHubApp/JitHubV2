namespace JitHub.WinUI.Automation;

/// <summary>
/// Missing/invalid timing evidence is absent, not an infinite JSON number or a
/// zero-cost pass. Aggregate gates separately require complete same-byte ratios.
/// </summary>
internal static class ReadmeAuditTimingRatio
{
    internal static double? Calculate(double nativeMilliseconds, double referenceMilliseconds)
    {
        if (!double.IsFinite(nativeMilliseconds) || nativeMilliseconds <= 0 ||
            !double.IsFinite(referenceMilliseconds) || referenceMilliseconds <= 0)
        {
            return null;
        }

        double ratio = nativeMilliseconds / referenceMilliseconds;
        return double.IsFinite(ratio) && ratio > 0 ? ratio : null;
    }
}
