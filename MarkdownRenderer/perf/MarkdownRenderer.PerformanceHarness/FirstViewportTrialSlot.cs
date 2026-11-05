namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Identifies one first-viewport trial: its condition, its ordinal within
/// the condition, its position in the global run and in the Williams
/// schedule, and whether it is a warmup.
/// </summary>
internal readonly record struct FirstViewportTrialSlot(
    FirstViewportScenario Scenario,
    int Ordinal,
    int GlobalOrdinal,
    int ScheduleRow,
    int SchedulePosition,
    bool IsWarmup);
