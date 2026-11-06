using System.Runtime.InteropServices;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Builds the numeric scheduling snapshot that the opt-in policy trace
/// records. The scheduler passes the slot and queue state it read under its
/// state lock.
/// </summary>
internal static class WorkerPolicySnapshots
{
    internal static SvgWorkerPolicySnapshot Create(
        int phase,
        long decisionSequence,
        bool energySaverOn,
        bool energySaverQueryFailed,
        bool canUseSecondary,
        bool shouldStartSecondary,
        WorkerSlot primary,
        WorkerSlot? secondary,
        int visibleOpenCount,
        int visibleRenderCount,
        long requestedFontGeneration)
    {
        WindowsWorkerProcess? primaryWorker = primary.Worker;
        WindowsWorkerProcess? secondaryWorker = secondary?.Worker;
        int secondaryReadinessState = secondary switch
        {
            null => 0,
            { Readiness.HasFailed: true } => 3,
            { Readiness.IsWarming: true } => 1,
            { Readiness.IsWorkerReady: true } => 2,
            _ => 1,
        };

        return new SvgWorkerPolicySnapshot(
            Phase: phase,
            DecisionSequence: decisionSequence,
            ProcessArchitecture: (int)RuntimeInformation.ProcessArchitecture,
            LogicalProcessorCount: Environment.ProcessorCount,
            EnergySaverOn: energySaverOn ? 1 : 0,
            EnergySaverQueryFailed: energySaverQueryFailed ? 1 : 0,
            CanUseSecondary: canUseSecondary ? 1 : 0,
            PrimaryBusy: primary.Busy ? 1 : 0,
            PrimaryWorkerState: GetWorkerProcessState(primaryWorker),
            PrimaryWorkerInstanceId: primaryWorker?.InstanceId ?? 0,
            PrimaryFontCatalogReady: primaryWorker is not null &&
                WorkerFontCatalogGeneration.IsReadyFor(primaryWorker.ReadyFontGeneration, requestedFontGeneration)
                    ? 1
                    : 0,
            VisibleOpenCount: visibleOpenCount,
            VisibleRenderCount: visibleRenderCount,
            SecondaryReadinessState: secondaryReadinessState,
            SecondaryBusy: secondary?.Busy == true ? 1 : 0,
            SecondaryWorkerState: GetWorkerProcessState(secondaryWorker),
            SecondaryWorkerInstanceId: secondaryWorker?.InstanceId ?? 0,
            SecondaryFontCatalogReady:
                secondaryWorker is not null && secondary?.Readiness is { } readiness &&
                WorkerFontCatalogGeneration.IsReadyFor(secondaryWorker.ReadyFontGeneration, requestedFontGeneration) &&
                WorkerFontCatalogGeneration.IsReadyFor(readiness.FontCatalogReadyGeneration, requestedFontGeneration)
                    ? 1
                    : 0,
            ShouldStartSecondary: shouldStartSecondary ? 1 : 0);
    }

    private static int GetWorkerProcessState(WindowsWorkerProcess? worker) => worker switch
    {
        null => 0,
        { HasExited: true } => 2,
        _ => 1,
    };
}
