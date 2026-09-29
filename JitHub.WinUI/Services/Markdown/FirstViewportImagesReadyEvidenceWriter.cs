using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace JitHub.Services.Markdown;

internal static class FirstViewportImagesReadyEvidenceWriter
{
    private static readonly object WriteGate = new();
    private static readonly object ProgressWriteGate = new();
    private static Task<bool>? _queuedWrite;
    private static Task? _queuedProgressWrite;
    private static string? _pendingProgressPath;
    private static FirstViewportImagesReadyProgress? _pendingProgress;

    internal static Task<bool>? QueuedWrite => _queuedWrite;

    internal static Task? QueuedProgressWrite
    {
        get
        {
            lock (ProgressWriteGate)
                return _queuedProgressWrite;
        }
    }

    internal static void TryQueueProgressWrite(
        string? path,
        bool auditEnabled,
        FirstViewportImagesReadyProgress progress)
    {
        if (!IsValidProgress(path, auditEnabled, progress))
            return;

        lock (ProgressWriteGate)
        {
            // Keep only the newest snapshot while one worker drains. The UI
            // thread never blocks on filesystem I/O and cannot build a queue
            // proportional to the 16 ms paint observer cadence.
            _pendingProgressPath = path;
            _pendingProgress = progress;
            if (_queuedProgressWrite is { IsCompleted: false })
                return;

            _queuedProgressWrite = Task.Run(DrainProgressWrites);
        }
    }

    internal static bool TryWriteProgress(
        string? path,
        bool auditEnabled,
        FirstViewportImagesReadyProgress progress)
    {
        if (!IsValidProgress(path, auditEnabled, progress))
            return false;

        try
        {
            string fullPath = Path.GetFullPath(path!);
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
                return false;

            Directory.CreateDirectory(directory);
            string entry = JsonSerializer.Serialize(
                progress,
                FirstViewportImagesReadyEvidenceJsonContext.Default.FirstViewportImagesReadyProgress);
            File.AppendAllText(fullPath, entry + Environment.NewLine);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static Task<bool>? TryQueueWrite(
        string? path,
        bool auditEnabled,
        int processId,
        string host,
        long generation,
        long viewportPaintGeneration,
        int pollCount,
        double probeWorkMilliseconds,
        double viewportTop,
        double viewportHeight,
        bool viewportMeasured,
        bool hasVisibleLoadingImages,
        string? readmeGitBlobSha1,
        DateTimeOffset timestamp)
    {
        if (!IsValid(
                path,
                auditEnabled,
                processId,
                host,
                generation,
                viewportPaintGeneration,
                pollCount,
                probeWorkMilliseconds,
                viewportTop,
                viewportHeight,
                viewportMeasured,
                hasVisibleLoadingImages))
        {
            return null;
        }

        // A readiness signal lets the audit begin its native capture, so make
        // sure the final ready-progress row has drained first. Capturing the
        // current task under the progress gate is nonblocking; the worker
        // awaits it off the UI thread before publishing the signal.
        Task? progressDrain;
        lock (ProgressWriteGate)
            progressDrain = _queuedProgressWrite;

        lock (WriteGate)
        {
            if (_queuedWrite is { IsCompleted: false })
                return null;

            _queuedWrite = Task.Run(() => WriteAfterProgressDrainAsync(
                progressDrain,
                () => TryWrite(
                    path,
                    auditEnabled,
                    processId,
                    host,
                    generation,
                    viewportPaintGeneration,
                    pollCount,
                    probeWorkMilliseconds,
                    viewportTop,
                    viewportHeight,
                    viewportMeasured,
                    hasVisibleLoadingImages,
                    readmeGitBlobSha1,
                    timestamp)));
            return _queuedWrite;
        }
    }

    internal static async Task<bool> WriteAfterProgressDrainAsync(
        Task? progressDrain,
        Func<bool> writeSignal)
    {
        ArgumentNullException.ThrowIfNull(writeSignal);
        if (progressDrain is not null)
            await progressDrain.ConfigureAwait(false);

        return writeSignal();
    }

    internal static bool TryWrite(
        string? path,
        bool auditEnabled,
        int processId,
        string host,
        long generation,
        long viewportPaintGeneration,
        int pollCount,
        double probeWorkMilliseconds,
        double viewportTop,
        double viewportHeight,
        bool viewportMeasured,
        bool hasVisibleLoadingImages,
        string? readmeGitBlobSha1,
        DateTimeOffset timestamp)
    {
        if (!IsValid(
                path,
                auditEnabled,
                processId,
                host,
                generation,
                viewportPaintGeneration,
                pollCount,
                probeWorkMilliseconds,
                viewportTop,
                viewportHeight,
                viewportMeasured,
                hasVisibleLoadingImages))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path!);
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
                return false;

            Directory.CreateDirectory(directory);
            string temporaryPath = fullPath + $".{processId}.tmp";
            var signal = new FirstViewportImagesReadySignal(
                processId,
                host,
                generation,
                viewportPaintGeneration,
                pollCount,
                probeWorkMilliseconds,
                viewportTop,
                viewportHeight,
                viewportMeasured,
                hasVisibleLoadingImages,
                readmeGitBlobSha1,
                timestamp);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    signal,
                    FirstViewportImagesReadyEvidenceJsonContext.Default.FirstViewportImagesReadySignal));
            File.Move(temporaryPath, fullPath, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsValid(
        string? path,
        bool auditEnabled,
        int processId,
        string host,
        long generation,
        long viewportPaintGeneration,
        int pollCount,
        double probeWorkMilliseconds,
        double viewportTop,
        double viewportHeight,
        bool viewportMeasured,
        bool hasVisibleLoadingImages) =>
        auditEnabled &&
        !string.IsNullOrWhiteSpace(path) &&
        processId > 0 &&
        !string.IsNullOrWhiteSpace(host) &&
        generation > 0 &&
        viewportPaintGeneration == generation &&
        pollCount > 0 &&
        double.IsFinite(probeWorkMilliseconds) &&
        probeWorkMilliseconds >= 0 &&
        FirstViewportImagesReadyProbeContract.IsMeasuredViewport(
            hasViewport: true,
            viewportTop,
            viewportHeight,
            viewportMeasured) &&
        !hasVisibleLoadingImages;

    private static void DrainProgressWrites()
    {
        while (true)
        {
            string? path;
            FirstViewportImagesReadyProgress? progress;
            lock (ProgressWriteGate)
            {
                path = _pendingProgressPath;
                progress = _pendingProgress;
                _pendingProgressPath = null;
                _pendingProgress = null;
                if (path is null || progress is null)
                {
                    _queuedProgressWrite = null;
                    return;
                }
            }

            _ = TryWriteProgress(path, auditEnabled: true, progress);
        }
    }

    private static bool IsValidProgress(
        string? path,
        bool auditEnabled,
        FirstViewportImagesReadyProgress progress) =>
        auditEnabled &&
        !string.IsNullOrWhiteSpace(path) &&
        progress is not null &&
        IsAllowedStage(progress.Stage) &&
        IsAllowedReason(progress.Reason) &&
        progress.PollCount >= 0 &&
        progress.PaintCallbackCount >= 0 &&
        progress.PaintedRegionCount >= 0 &&
        progress.Generation >= 0 &&
        progress.CurrentGeneration >= 0 &&
        progress.PublishedGeneration >= 0 &&
        progress.SnapshotGeneration >= 0 &&
        progress.LayoutRevision >= 0 &&
        progress.VisibleLoadingImageCount >= 0 &&
        progress.PendingImageRegionCount >= 0 &&
        progress.UncoveredRegionCount >= 0 &&
        double.IsFinite(progress.RasterizationScale) && progress.RasterizationScale >= 0 &&
        double.IsFinite(progress.ViewportLeft) &&
        double.IsFinite(progress.ViewportTop) &&
        double.IsFinite(progress.ViewportWidth) && progress.ViewportWidth >= 0 &&
        double.IsFinite(progress.ViewportHeight) && progress.ViewportHeight >= 0 &&
        double.IsFinite(progress.CoveredArea) && progress.CoveredArea >= 0 &&
        double.IsFinite(progress.ViewportArea) && progress.ViewportArea >= 0 &&
        progress.CoveredArea <= progress.ViewportArea + 0.01 &&
        double.IsFinite(progress.LastPaintedIntersectionArea) && progress.LastPaintedIntersectionArea >= 0 &&
        double.IsFinite(progress.LastPaintedRegionLeft) &&
        double.IsFinite(progress.LastPaintedRegionTop) &&
        double.IsFinite(progress.LastPaintedRegionWidth) && progress.LastPaintedRegionWidth >= 0 &&
        double.IsFinite(progress.LastPaintedRegionHeight) && progress.LastPaintedRegionHeight >= 0 &&
        progress.HasPostArmPaintedRegion == (progress.PaintedRegionCount > 0);

    private static bool IsAllowedStage(string stage) => stage is
        "armed" or "polling" or "paint-callback" or "ready" or "stopped";

    private static bool IsAllowedReason(string reason) => reason is
        "not-started" or
        "waiting-for-paint" or
        "waiting-for-published-generation" or
        "waiting-for-measured-viewport" or
        "visible-images-loading" or
        "waiting-for-visible-image-paint" or
        "waiting-for-viewport-coverage" or
        "viewport-covered-after-ready-paint" or
        "generation-or-measured-viewport-mismatch" or
        "invalid-rasterization-scale" or
        "invalid-coverage-identity" or
        "invalid-coverage-region" or
        "invalid-pending-image-mask" or
        "pending-mask-count-budget-exceeded" or
        "coverage-region-budget-exceeded" or
        "coverage-work-budget-exceeded" or
        "probe-stopped";
}

internal sealed record FirstViewportImagesReadyProgress(
    DateTimeOffset Timestamp,
    string Stage,
    string Reason,
    int PollCount,
    long PaintCallbackCount,
    long PaintedRegionCount,
    long Generation,
    long CurrentGeneration,
    long PublishedGeneration,
    long SnapshotGeneration,
    long LayoutRevision,
    double RasterizationScale,
    double ViewportLeft,
    double ViewportTop,
    double ViewportWidth,
    double ViewportHeight,
    bool ViewportMeasured,
    bool HasVisibleLoadingImages,
    int VisibleLoadingImageCount,
    int PendingImageRegionCount,
    double CoveredArea,
    double ViewportArea,
    int UncoveredRegionCount,
    double LastPaintedIntersectionArea,
    double LastPaintedRegionLeft,
    double LastPaintedRegionTop,
    double LastPaintedRegionWidth,
    double LastPaintedRegionHeight,
    bool HasPostArmPaintedRegion);

internal sealed record FirstViewportImagesReadySignal(
    int ProcessId,
    string Host,
    long Generation,
    long ViewportPaintGeneration,
    int PollCount,
    double ProbeWorkMilliseconds,
    double ViewportTop,
    double ViewportHeight,
    bool ViewportMeasured,
    bool HasVisibleLoadingImages,
    string? ReadmeGitBlobSha1,
    DateTimeOffset Timestamp);

internal static class FirstViewportImagesReadyProbeContract
{
    internal static bool IsCurrentPublishedGeneration(
        long requestedGeneration,
        long currentPipelineGeneration,
        long publishedPipelineGeneration) =>
        requestedGeneration > 0 &&
        requestedGeneration == currentPipelineGeneration &&
        requestedGeneration == publishedPipelineGeneration;

    internal static bool IsMeasuredViewport(
        bool hasViewport,
        double viewportTop,
        double viewportHeight,
        bool viewportMeasured) =>
        hasViewport &&
        double.IsFinite(viewportTop) &&
        viewportTop >= 0 &&
        double.IsFinite(viewportHeight) &&
        viewportHeight > 0 &&
        viewportMeasured;

    internal static bool IsViewportPaintAcknowledged(
        long requestedGeneration,
        long paintedGeneration,
        bool hasVisibleLoadingImages) =>
        requestedGeneration > 0 &&
        paintedGeneration == requestedGeneration &&
        !hasVisibleLoadingImages;
}

[JsonSerializable(typeof(FirstViewportImagesReadySignal))]
[JsonSerializable(typeof(FirstViewportImagesReadyProgress))]
internal sealed partial class FirstViewportImagesReadyEvidenceJsonContext : JsonSerializerContext
{
}
