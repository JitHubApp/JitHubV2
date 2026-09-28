using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace JitHub.Services.Markdown;

internal static class FirstViewportImagesReadyEvidenceWriter
{
    private static readonly object WriteGate = new();
    private static Task<bool>? _queuedWrite;

    internal static Task<bool>? QueuedWrite => _queuedWrite;

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

        lock (WriteGate)
        {
            if (_queuedWrite is { IsCompleted: false })
                return null;

            _queuedWrite = Task.Run(() => TryWrite(
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
                timestamp));
            return _queuedWrite;
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
}

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
internal sealed partial class FirstViewportImagesReadyEvidenceJsonContext : JsonSerializerContext
{
}
