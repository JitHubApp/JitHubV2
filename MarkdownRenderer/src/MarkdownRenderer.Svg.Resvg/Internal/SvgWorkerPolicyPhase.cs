namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>Phases of one secondary-worker scheduling decision.</summary>
internal enum SvgWorkerPolicyPhase
{
    Evaluated = 1,
    StartRequested = 2,
    WorkerLaunchStarted = 3,
    WorkerProcessStarted = 4,
    Ready = 5,
    Failed = 6,
    TraceLimitReached = 7,
}
