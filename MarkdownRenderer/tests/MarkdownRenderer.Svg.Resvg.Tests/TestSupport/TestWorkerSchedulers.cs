using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using MarkdownRenderer.Svg.Resvg.Internal.Diagnostics;
using MarkdownRenderer.Svg.Resvg.Internal.Workers;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.FontReadiness;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Transport;

namespace MarkdownRenderer.Svg.Resvg.Tests.TestSupport;

/// <summary>
/// Builds a <see cref="WorkerScheduler"/> configured as <see cref="WorkerPool"/>
/// configures its own, for admission tests that never start a worker.
/// </summary>
internal static class TestWorkerSchedulers
{
    internal static WorkerScheduler Create(
        Func<bool>? isEnergySaverOn = null,
        SvgWorkerPolicyTrace? workerPolicyTrace = null,
        ResvgMarkdownSvgRendererOptions? options = null,
        DetachedFontWaitLedger? detachedFontWaits = null)
    {
        options ??= new ResvgMarkdownSvgRendererOptions();
        bool supportsSecondaryHardware = WorkerSchedulingPolicy.CanUseSecondary(
            RuntimeInformation.ProcessArchitecture,
            Environment.ProcessorCount,
            energySaverOn: false);
        return new WorkerScheduler(
            options,
            new WorkerRequestFactory(options, options.ParsedResourceCacheBytes),
            new WindowsWorkerProcessLauncher(
                Path.Combine(AppContext.BaseDirectory, WorkerProtocol.WorkerFileName),
                options),
            TimeProvider.System,
            isEnergySaverOn ?? (static () => true),
            supportsSecondaryHardware,
            workerPolicyTrace ?? new SvgWorkerPolicyTrace(),
            detachedFontWaits);
    }

    internal static Task<WorkerLease> AcquireAsync(
        WorkerScheduler scheduler,
        CancellationToken cancellationToken,
        WorkerOperation operation = WorkerOperation.Render,
        MarkdownSvgRenderPriority priority = MarkdownSvgRenderPriority.Visible,
        bool requiresFontCatalog = false,
        WorkerFontWaitReservation? fontWaitReservation = null,
        MarkdownSvgRenderDemand? demandTicket = null) =>
        scheduler.AcquireAsync(
            operation,
            priority,
            cancellationToken,
            requiresFontCatalog,
            preferredWorkerInstanceId: 0,
            isSecondaryFontProbe: false,
            fontWaitReservation,
            demandTicket);
}
