#if MARKDOWN_LAYOUT_DIAGNOSTICS
namespace MarkdownRenderer.Diagnostics;

/// <summary>
/// Numeric-only snapshot of the optional SVG secondary-worker scheduling
/// policy. Phase and worker-state values are documented by the producer; no
/// document identity, SVG source, URL, account, or credential is included.
/// PrimaryWorkerState and SecondaryWorkerState use 0=absent, 1=alive,
/// 2=exited. SecondaryReadinessState uses 0=absent, 1=warming, 2=ready,
/// 3=failed. EnergySaverOn and all Boolean fields use 0=false, 1=true.
/// </summary>
internal readonly record struct MarkdownSvgWorkerPolicySnapshot(
    int Phase,
    long DecisionSequence,
    int ProcessArchitecture,
    int LogicalProcessorCount,
    int EnergySaverOn,
    int EnergySaverQueryFailed,
    int CanUseSecondary,
    int PrimaryBusy,
    int PrimaryWorkerState,
    long PrimaryWorkerInstanceId,
    int PrimaryFontCatalogReady,
    int VisibleOpenCount,
    int VisibleRenderCount,
    int SecondaryReadinessState,
    int SecondaryBusy,
    int SecondaryWorkerState,
    long SecondaryWorkerInstanceId,
    int SecondaryFontCatalogReady,
    int ShouldStartSecondary,
    long EventSequence = 0,
    int ProcessId = 0,
    long StopwatchTicks = 0,
    long StopwatchFrequency = 0);
#endif
