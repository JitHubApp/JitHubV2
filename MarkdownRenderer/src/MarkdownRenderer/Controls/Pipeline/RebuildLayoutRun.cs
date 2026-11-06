using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// The layout stage of a rebuild: builds the snapshot on one worker, through
/// the performance session's scheduler when one is attached, either whole or
/// lazily around the viewport.
/// </summary>
internal static class RebuildLayoutRun
{
    /// <summary>
    /// Builds the snapshot of <paramref name="parsed"/> at <paramref name="width"/>.
    /// Returns null when the build was cancelled before it produced a result.
    /// <paramref name="captureCpu"/> measures the worker's CPU time, and
    /// <paramref name="workerCompleted"/> is called on the worker with its end
    /// timestamp.
    /// </summary>
    internal static async Task<RebuildLayoutRunResult?> RunAsync(
        LayoutBuilder builder,
        MarkdownLayoutContext ctx,
        ParsedMarkdown parsed,
        float width,
        double viewportTop,
        double viewportHeight,
        bool hasCustomBlockEmbedMeasurement,
        IMarkdownPerformanceSessionInternal? performanceSession,
        bool captureCpu,
        Action<long>? workerCompleted,
        CancellationToken ct)
    {
        bool useLazyLayout = ShouldUseLazyLayout(parsed, hasCustomBlockEmbedMeasurement, performanceSession);
        double initialLazyOverscan = GetInitialLazyOverscan(viewportHeight, performanceSession);
        long layoutStarted = Stopwatch.GetTimestamp();
        Func<CancellationToken, (LayoutSnapshot? Snapshot, double CpuMilliseconds, long Started, long Ended)> layoutWork =
            layoutToken =>
            {
                ctx.SetCancellationToken(layoutToken);
                long started = Stopwatch.GetTimestamp();
                // Keep this synchronous build on one worker. Its CPU time
                // separates layout work from queueing/descheduling when a
                // profile reports an anomalous wall-clock stall.
                long cpuStarted = !captureCpu
                    ? -1
                    : ThreadCpuClock.ReadCurrentThreadTicks();
                LayoutSnapshot? built = BuildOrNullOnCancellation(
                    builder,
                    parsed.Document,
                    width,
                    viewportTop,
                    viewportHeight,
                    useLazyLayout,
                    initialLazyOverscan,
                    layoutToken);
                long cpuEnded = cpuStarted < 0
                    ? -1
                    : ThreadCpuClock.ReadCurrentThreadTicks();
                long ended = Stopwatch.GetTimestamp();
                workerCompleted?.Invoke(ended);
                return (built, ThreadCpuClock.ElapsedMilliseconds(cpuStarted, cpuEnded), started, ended);
            };

        (LayoutSnapshot? Snapshot, double CpuMilliseconds, long Started, long Ended) layoutResult;
        try
        {
            Task<(LayoutSnapshot? Snapshot, double CpuMilliseconds, long Started, long Ended)> layoutTask =
                performanceSession is null
                    ? Task.Run(() => layoutWork(ct), CancellationToken.None)
                    : performanceSession.ScheduleLayoutWorkAsync(
                        MarkdownLayoutWorkPriority.Primary,
                        layoutWork,
                        ct);
            layoutResult = await layoutTask.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (
            ct.IsCancellationRequested || performanceSession?.IsDisposed == true)
        {
            return null;
        }

        var (snapshot, cpuMilliseconds, workerStarted, workerEnded) = layoutResult;
        return new RebuildLayoutRunResult(
            snapshot,
            cpuMilliseconds,
            layoutStarted,
            workerStarted,
            workerEnded,
            Stopwatch.GetTimestamp());
    }

    private static bool ShouldUseLazyLayout(
        ParsedMarkdown parsed,
        bool hasCustomBlockEmbedMeasurement,
        IMarkdownPerformanceSessionInternal? performanceSession)
    {
        // Custom block embed measurement remains on the background build path.
        // Subsequent lazy-band extension is also dispatched to a background
        // worker, so IMarkdownEmbedFactory.MeasureHeight and text reflow never
        // run from viewport or paint callbacks.
        bool visibleFirstVectorAdmissionRequested = performanceSession is
            { IsDisposed: false, Options.DeferOffscreenScenes: true };
        return visibleFirstVectorAdmissionRequested || MarkdownLazyLayoutPolicy.ShouldUse(
            parsed.Document.Count,
            parsed.SourceText.Length,
            hasCustomBlockEmbedMeasurement);
    }

    private static double GetInitialLazyOverscan(
        double viewportHeight,
        IMarkdownPerformanceSessionInternal? performanceSession) =>
        performanceSession is null
            ? MarkdownLazyLayoutPolicy.OverscanPx
            : Math.Clamp(
                viewportHeight * performanceSession.Options.LookAheadViewports,
                0,
                6000);

    private static LayoutSnapshot? BuildOrNullOnCancellation(
        LayoutBuilder builder,
        Markdig.Syntax.MarkdownDocument document,
        float width,
        double viewportTop,
        double viewportHeight,
        bool useLazyLayout,
        double overscan,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return null;

        try
        {
            return useLazyLayout
                ? builder.BuildLazyForViewport(document, width, viewportTop, viewportHeight, overscan, ct)
                : builder.Build(document, width, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
