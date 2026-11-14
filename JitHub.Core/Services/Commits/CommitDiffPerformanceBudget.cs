using System;

namespace JitHub.Core.Services.Commits;

public static class CommitDiffPerformanceBudget
{
    public static double CalculateDispatcherLateness(TimeSpan elapsed, TimeSpan expectedInterval) =>
        Math.Max(0, (elapsed - expectedInterval).TotalMilliseconds);
}
