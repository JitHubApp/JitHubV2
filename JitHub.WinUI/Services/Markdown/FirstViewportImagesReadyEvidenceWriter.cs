using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services.Markdown;

internal static class FirstViewportImagesReadyEvidenceWriter
{
    private static readonly object WriteGate = new();
    private static readonly object ProgressWriteGate = new();
    private static readonly object PublicationWriteGate = new();
    private static Task<bool>? _queuedWrite;
    private static SignalWriteRequest? _pendingWrite;
    private static Task? _queuedProgressWrite;
    private static string? _pendingProgressPath;
    private static FirstViewportImagesReadyProgress? _pendingProgress;
    private const int MaximumPendingPublicationOutcomes = 32;
    private const int MaximumPublicationRecordBytes = 4096;
    private static readonly Queue<PublicationOutcomeWriteRequest> PendingPublicationOutcomes = new();
    private static Task? _queuedPublicationWrite;

    internal static Task<bool>? QueuedWrite => _queuedWrite;

    internal static Task? QueuedProgressWrite
    {
        get
        {
            lock (ProgressWriteGate)
                return _queuedProgressWrite;
        }
    }

    internal static Task? QueuedPublicationWrite
    {
        get
        {
            lock (PublicationWriteGate)
                return _queuedPublicationWrite;
        }
    }

    internal static string GetPublicationOutcomePath(string progressPath) =>
        progressPath + ".publication.ndjson";

    // Terminal observations are never coalesced with paint polling. The queue
    // is bounded, best-effort diagnostic evidence, not a readiness signal.
    internal static bool TryQueuePublicationOutcome(
        string? path,
        bool auditEnabled,
        FirstViewportImagesReadyPublicationOutcomeRow outcome)
    {
        if (!IsValidPublicationOutcome(path, auditEnabled, outcome))
            return false;

        lock (PublicationWriteGate)
        {
            if (PendingPublicationOutcomes.Count >= MaximumPendingPublicationOutcomes)
                return false;

            PendingPublicationOutcomes.Enqueue(new(GetPublicationOutcomePath(path!), outcome));
            if (_queuedPublicationWrite is not { IsCompleted: false })
                _queuedPublicationWrite = Task.Run(DrainPublicationWrites);
            return true;
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
        DateTimeOffset timestamp,
        long stopwatchTicks,
        long stopwatchFrequency,
        CancellationToken generationCancellationToken = default,
        FirstViewportImagesReadyPublicationIdentity? publicationIdentity = null,
        string? publicationProgressPath = null)
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
                hasVisibleLoadingImages,
                stopwatchTicks,
                stopwatchFrequency))
        {
            return null;
        }

        if (generationCancellationToken.IsCancellationRequested)
        {
            RecordPublicationOutcome(publicationIdentity, publicationProgressPath, PublicationWriteOutcome.Canceled);
            return null;
        }

        // A readiness signal lets the audit begin its native capture, so make
        // sure the final ready-progress row has drained first. Capturing the
        // current task under the progress gate is nonblocking; the worker
        // awaits it off the UI thread before publishing the signal.
        Task? progressDrain;
        lock (ProgressWriteGate)
            progressDrain = _queuedProgressWrite;

        return QueueWriteOutcomeAfterCurrent(
            progressDrain,
            () => TryWriteOutcome(
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
                timestamp,
                stopwatchTicks,
                stopwatchFrequency,
                generationCancellationToken),
            generationCancellationToken,
            publicationIdentity,
            publicationProgressPath);
    }

    internal static Task<bool> QueueWriteAfterCurrent(
        Task? progressDrain,
        Func<bool> writeSignal,
        CancellationToken generationCancellationToken = default,
        FirstViewportImagesReadyPublicationIdentity? publicationIdentity = null,
        string? publicationProgressPath = null)
    {
        ArgumentNullException.ThrowIfNull(writeSignal);
        return QueueWriteOutcomeAfterCurrent(progressDrain,
            () => writeSignal() ? PublicationWriteOutcome.Published : PublicationWriteOutcome.WriteFailed,
            generationCancellationToken, publicationIdentity, publicationProgressPath);
    }

    private static Task<bool> QueueWriteOutcomeAfterCurrent(
        Task? progressDrain,
        Func<PublicationWriteOutcome> writeSignal,
        CancellationToken generationCancellationToken,
        FirstViewportImagesReadyPublicationIdentity? publicationIdentity,
        string? publicationProgressPath)
    {
        var request = new SignalWriteRequest(
            progressDrain, writeSignal, generationCancellationToken,
            publicationIdentity, publicationProgressPath);
        SignalWriteRequest? suppressed = null;
        PublicationWriteOutcome suppressedOutcome = PublicationWriteOutcome.Suppressed;
        bool canceled;

        lock (WriteGate)
        {
            canceled = generationCancellationToken.IsCancellationRequested;
            if (canceled)
                request.Completion.TrySetResult(false);
            else if (_queuedWrite is { IsCompleted: false })
            {
                // At most one newest-generation write waits behind the active
                // writer. Replacing the pending request completes its waiter
                // as suppressed instead of growing an unbounded queue.
                suppressed = _pendingWrite;
                if (suppressed?.GenerationCancellationToken.IsCancellationRequested is true)
                    suppressedOutcome = PublicationWriteOutcome.Canceled;
                suppressed?.Completion.TrySetResult(false);
                _pendingWrite = request;
            }
            else
            {
                _queuedWrite = Task.Run(() => DrainQueuedWritesAsync(request));
            }
        }

        // Do not nest the signal and progress queue locks or perform I/O on
        // the caller. A superseded request is not a failed filesystem write.
        suppressed?.RecordOutcome(suppressedOutcome);
        if (canceled)
            request.RecordOutcome(PublicationWriteOutcome.Canceled);
        return request.Completion.Task;
    }

    internal static async Task<bool> WriteAfterProgressDrainAsync(
        Task? progressDrain,
        Func<bool> writeSignal,
        CancellationToken generationCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeSignal);
        if (progressDrain is not null)
        {
            try
            {
                await progressDrain.WaitAsync(generationCancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (generationCancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        return !generationCancellationToken.IsCancellationRequested && writeSignal();
    }

    private static async Task<bool> DrainQueuedWritesAsync(SignalWriteRequest firstRequest)
    {
        SignalWriteRequest? request = firstRequest;
        bool lastResult = false;
        while (request is not null)
        {
            PublicationWriteOutcome outcome;
            try
            {
                outcome = await WriteOutcomeAfterProgressDrainAsync(
                    request.ProgressDrain,
                    request.WriteSignal,
                    request.GenerationCancellationToken).ConfigureAwait(false);
                lastResult = outcome == PublicationWriteOutcome.Published;
            }
            catch (Exception)
            {
                lastResult = false;
                outcome = PublicationWriteOutcome.Faulted;
            }

            request.RecordOutcome(outcome);
            request.Completion.TrySetResult(lastResult);
            lock (WriteGate)
            {
                request = _pendingWrite;
                _pendingWrite = null;
                if (request is null)
                {
                    _queuedWrite = null;
                    return lastResult;
                }
            }
        }

        return lastResult;
    }

    private static async Task<PublicationWriteOutcome> WriteOutcomeAfterProgressDrainAsync(
        Task? progressDrain, Func<PublicationWriteOutcome> writeSignal, CancellationToken cancellationToken)
    {
        if (progressDrain is not null)
        {
            try { await progressDrain.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { return PublicationWriteOutcome.Canceled; }
        }
        return cancellationToken.IsCancellationRequested
            ? PublicationWriteOutcome.Canceled : writeSignal();
    }

    private enum PublicationWriteOutcome { Published, WriteFailed, Canceled, Suppressed, Faulted }

    private sealed class SignalWriteRequest
    {
        internal SignalWriteRequest(
            Task? progressDrain,
            Func<PublicationWriteOutcome> writeSignal,
            CancellationToken generationCancellationToken,
            FirstViewportImagesReadyPublicationIdentity? publicationIdentity,
            string? publicationProgressPath)
        {
            ProgressDrain = progressDrain;
            WriteSignal = writeSignal;
            GenerationCancellationToken = generationCancellationToken;
            PublicationIdentity = publicationIdentity;
            PublicationProgressPath = publicationProgressPath;
        }

        internal Task? ProgressDrain { get; }
        internal Func<PublicationWriteOutcome> WriteSignal { get; }
        internal CancellationToken GenerationCancellationToken { get; }
        private FirstViewportImagesReadyPublicationIdentity? PublicationIdentity { get; }
        private string? PublicationProgressPath { get; }
        private readonly TaskCompletionSource<bool> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Completion => _completion;

        internal void RecordOutcome(PublicationWriteOutcome outcome) =>
            RecordPublicationOutcome(PublicationIdentity, PublicationProgressPath, outcome);
    }

    private static void RecordPublicationOutcome(
        FirstViewportImagesReadyPublicationIdentity? publicationIdentity,
        string? publicationProgressPath,
        PublicationWriteOutcome outcome)
    {
        if (publicationIdentity is not { } identity)
            return;

        // Values only: no viewer, renderer, dispatcher, Markdown payload or
        // exception text survives the asynchronous write boundary.
        try
        {
            _ = TryQueuePublicationOutcome(
                publicationProgressPath,
                auditEnabled: true,
                new(1, "readiness-publication-outcome", identity.ProcessId,
                    identity.Host, identity.Generation, identity.ViewportPaintGeneration,
                    identity.Source, outcome switch
                    {
                        PublicationWriteOutcome.Published => "published",
                        PublicationWriteOutcome.WriteFailed => "write-failed",
                        PublicationWriteOutcome.Canceled => "canceled",
                        PublicationWriteOutcome.Suppressed => "suppressed",
                        _ => "faulted",
                    }, DateTimeOffset.UtcNow));
        }
        catch (Exception)
        {
            // Optional diagnostics must not strand the signal completion
            // or turn queue/resource pressure into an app exception.
        }
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
        DateTimeOffset timestamp,
        long stopwatchTicks,
        long stopwatchFrequency,
        CancellationToken generationCancellationToken = default)
        => TryWriteOutcome(path, auditEnabled, processId, host, generation,
            viewportPaintGeneration, pollCount, probeWorkMilliseconds, viewportTop,
            viewportHeight, viewportMeasured, hasVisibleLoadingImages, readmeGitBlobSha1,
            timestamp, stopwatchTicks, stopwatchFrequency, generationCancellationToken)
            == PublicationWriteOutcome.Published;

    private static PublicationWriteOutcome TryWriteOutcome(
        string? path, bool auditEnabled, int processId, string host, long generation,
        long viewportPaintGeneration, int pollCount, double probeWorkMilliseconds,
        double viewportTop, double viewportHeight, bool viewportMeasured,
        bool hasVisibleLoadingImages, string? readmeGitBlobSha1, DateTimeOffset timestamp,
        long stopwatchTicks, long stopwatchFrequency, CancellationToken generationCancellationToken)
    {
        if (generationCancellationToken.IsCancellationRequested)
            return PublicationWriteOutcome.Canceled;
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
                hasVisibleLoadingImages,
                stopwatchTicks,
                stopwatchFrequency))
        {
            return PublicationWriteOutcome.WriteFailed;
        }

        try
        {
            string fullPath = Path.GetFullPath(path!);
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
                return PublicationWriteOutcome.WriteFailed;

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
                timestamp,
                stopwatchTicks,
                stopwatchFrequency);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    signal,
                    FirstViewportImagesReadyEvidenceJsonContext.Default.FirstViewportImagesReadySignal));
            if (generationCancellationToken.IsCancellationRequested)
            {
                File.Delete(temporaryPath);
                return PublicationWriteOutcome.Canceled;
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            return PublicationWriteOutcome.Published;
        }
        catch (IOException)
        {
            return PublicationWriteOutcome.WriteFailed;
        }
        catch (UnauthorizedAccessException)
        {
            return PublicationWriteOutcome.WriteFailed;
        }
        catch (Exception)
        {
            return PublicationWriteOutcome.WriteFailed;
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
        bool hasVisibleLoadingImages,
        long stopwatchTicks,
        long stopwatchFrequency) =>
        auditEnabled &&
        !string.IsNullOrWhiteSpace(path) &&
        processId > 0 &&
        !string.IsNullOrWhiteSpace(host) &&
        generation > 0 &&
        viewportPaintGeneration == generation &&
        stopwatchTicks > 0 &&
        stopwatchFrequency > 0 &&
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

    private static void DrainPublicationWrites()
    {
        while (true)
        {
            PublicationOutcomeWriteRequest publication;
            lock (PublicationWriteGate)
            {
                if (PendingPublicationOutcomes.Count == 0)
                {
                    _queuedPublicationWrite = null;
                    return;
                }
                publication = PendingPublicationOutcomes.Dequeue();
            }
            _ = TryWritePublicationOutcome(publication);
        }
    }

    private static bool TryWritePublicationOutcome(PublicationOutcomeWriteRequest request)
    {
        try
        {
            string entry = JsonSerializer.Serialize(request.Outcome,
                FirstViewportImagesReadyEvidenceJsonContext.Default.FirstViewportImagesReadyPublicationOutcomeRow);
            if (Encoding.UTF8.GetByteCount(entry) > MaximumPublicationRecordBytes)
                return false;
            string fullPath = Path.GetFullPath(request.Path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
                return false;
            Directory.CreateDirectory(directory);
            File.AppendAllText(fullPath, entry + Environment.NewLine);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsValidPublicationOutcome(
        string? path, bool auditEnabled, FirstViewportImagesReadyPublicationOutcomeRow outcome)
    {
        if (!auditEnabled || string.IsNullOrWhiteSpace(path) || outcome is null ||
            outcome.SchemaVersion != 1 || outcome.RecordKind != "readiness-publication-outcome" ||
            outcome.ProcessId <= 0 || string.IsNullOrWhiteSpace(outcome.Host) || outcome.Host.Length > 256 ||
            outcome.Generation <= 0 || outcome.ViewportPaintGeneration != outcome.Generation ||
            outcome.Outcome is not ("published" or "write-failed" or "faulted" or "canceled" or "suppressed"))
            return false;

        return outcome.SameByteReadmeSourceAdmission is not { } source ||
            (source.ProcessId == outcome.ProcessId && source.Generation == outcome.Generation &&
             string.Equals(source.Host, outcome.Host, StringComparison.Ordinal) &&
             source.RawByteSize >= 0 && source.DecodedTextUtf8ByteSize >= 0 &&
             IsHexDigest(source.RawSha256, 64) && IsHexDigest(source.DecodedTextSha256, 64) &&
             IsHexDigest(source.ReadmeGitBlobSha1, 40) &&
             source.SourceOwner?.Length is not > 256 && source.SourceRepository?.Length is not > 256 &&
             source.SourceRef?.Length is not > 1024 && source.SourcePath?.Length is not > 2048);
    }

    private static bool IsHexDigest(string? value, int length)
    {
        if (value is null || value.Length != length)
            return false;
        foreach (char character in value)
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
                return false;
        return true;
    }

    private sealed record PublicationOutcomeWriteRequest(
        string Path, FirstViewportImagesReadyPublicationOutcomeRow Outcome);

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
        progress.PaintRequestedGeneration >= 0 &&
        progress.PaintedGeneration >= 0 &&
        progress.PaintedAtStopwatchTicks >= 0 &&
        progress.PaintPollAction >= (int)FirstViewportImagesReadyPaintPollAction.NotPolled &&
        progress.PaintPollAction <= (int)FirstViewportImagesReadyPaintPollAction.Ready &&
        ((progress.PaintedGeneration == 0) == (progress.PaintedAtStopwatchTicks == 0)) &&
        (progress.PaintAcknowledgementRecorded is not true ||
            (progress.PaintProbeAccepted is true && progress.PaintedGeneration > 0 && progress.PaintedAtStopwatchTicks > 0)) &&
        (progress.PaintAcknowledgementIsCurrent is not true ||
            (progress.PaintedGeneration > 0 && progress.PaintedAtStopwatchTicks > 0)) &&
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
        "validating-paint" or
        "painted-snapshot-no-longer-current" or
        "painted-snapshot-identity-changed" or
        "visible-deferred-scene-pending-or-unverified" or
        "waiting-for-paint" or
        "waiting-for-published-generation" or
        "waiting-for-measured-viewport" or
        "visible-images-loading" or
        "waiting-for-visible-image-paint" or
        "waiting-for-viewport-coverage" or
        "paint-rearmed" or
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
    bool HasPostArmPaintedRegion,
    bool? PaintProbeAccepted = null,
    bool? PaintAcknowledgementRecorded = null,
    long PaintRequestedGeneration = 0,
    long PaintedGeneration = 0,
    long PaintedAtStopwatchTicks = 0,
    bool? PaintAcknowledgementIsCurrent = null,
    int PaintPollAction = (int)FirstViewportImagesReadyPaintPollAction.NotPolled);

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
    DateTimeOffset Timestamp,
    long StopwatchTicks,
    long StopwatchFrequency);

internal sealed record FirstViewportImagesReadyPublicationIdentity(
    int ProcessId,
    string Host,
    long Generation,
    long ViewportPaintGeneration,
    SameByteReadmeSourceAdmission? Source = null);

internal sealed record FirstViewportImagesReadyPublicationOutcomeRow(
    int SchemaVersion,
    string RecordKind,
    int ProcessId,
    string Host,
    long Generation,
    long ViewportPaintGeneration,
    SameByteReadmeSourceAdmission? SameByteReadmeSourceAdmission,
    string Outcome,
    DateTimeOffset Timestamp);

internal static class FirstViewportImagesReadyProbeContract
{
    internal static bool IsPaintedSnapshotIdentityCurrent(
        object? currentSnapshot,
        object? paintedSnapshot,
        long currentLayoutRevision,
        long paintedLayoutRevision) =>
        paintedSnapshot is not null &&
        ReferenceEquals(currentSnapshot, paintedSnapshot) &&
        currentLayoutRevision == paintedLayoutRevision;

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
[JsonSerializable(typeof(FirstViewportImagesReadyPublicationOutcomeRow))]
internal sealed partial class FirstViewportImagesReadyEvidenceJsonContext : JsonSerializerContext
{
}
