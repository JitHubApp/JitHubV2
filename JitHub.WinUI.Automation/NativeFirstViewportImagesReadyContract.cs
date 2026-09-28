using System;
using System.IO;
using System.Text.Json;

internal static class NativeFirstViewportImagesReadyContract
{
    internal static NativeLifecycleReadySignal ReadLifecycleReadySignal(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        return new NativeLifecycleReadySignal(
            ReadRequiredInt32(root, "ProcessId", path),
            ReadRequiredString(root, "Stage", path),
            ReadRequiredTimestamp(root, "Timestamp", path));
    }

    internal static NativeRenderCompleteSignal ReadRenderCompleteSignal(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        return new NativeRenderCompleteSignal(
            ReadRequiredInt32(root, "ProcessId", path),
            ReadRequiredString(root, "Host", path),
            ReadRequiredInt64(root, "Generation", path),
            ReadRequiredTimestamp(root, "Timestamp", path));
    }

    internal static NativeFirstViewportImagesReadySignal ReadImagesReadySignal(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        JsonElement loadingImages = ReadRequiredProperty(root, "HasVisibleLoadingImages", path);
        if (loadingImages.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"Native first-viewport readiness signal '{path}' does not contain a valid HasVisibleLoadingImages value.");
        }
        JsonElement viewportMeasured = ReadRequiredProperty(root, "ViewportMeasured", path);
        if (viewportMeasured.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"Native first-viewport readiness signal '{path}' does not contain a valid ViewportMeasured value.");
        }

        string? readmeGitBlobSha1 = null;
        JsonElement sha = ReadRequiredProperty(root, "ReadmeGitBlobSha1", path);
        if (sha.ValueKind == JsonValueKind.String)
            readmeGitBlobSha1 = sha.GetString();
        else if (sha.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException(
                $"Native first-viewport readiness signal '{path}' has an invalid README git blob SHA.");

        return new NativeFirstViewportImagesReadySignal(
            ReadRequiredInt32(root, "ProcessId", path),
            ReadRequiredString(root, "Host", path),
            ReadRequiredInt64(root, "Generation", path),
            ReadRequiredInt64(root, "ViewportPaintGeneration", path),
            ReadRequiredInt32(root, "PollCount", path),
            ReadRequiredDouble(root, "ProbeWorkMilliseconds", path),
            ReadRequiredDouble(root, "ViewportTop", path),
            ReadRequiredDouble(root, "ViewportHeight", path),
            viewportMeasured.GetBoolean(),
            loadingImages.GetBoolean(),
            readmeGitBlobSha1,
            ReadRequiredTimestamp(root, "Timestamp", path));
    }

    internal static void ValidateRenderIdentity(
        int processId,
        string expectedHost,
        NativeLifecycleReadySignal hostReady,
        NativeRenderCompleteSignal renderComplete)
    {
        if (hostReady.ProcessId != processId ||
            !string.Equals(hostReady.Stage, expectedHost, StringComparison.Ordinal) ||
            renderComplete.ProcessId != processId ||
            !string.Equals(renderComplete.Host, expectedHost, StringComparison.Ordinal) ||
            renderComplete.Generation <= 0 ||
            renderComplete.Timestamp < hostReady.Timestamp)
        {
            throw new InvalidDataException(
                "The native host-ready and render-complete signals did not identify the launched process, requested host, and monotonic render generation.");
        }
    }

    internal static void ValidateImagesReadySignal(
        int processId,
        string expectedHost,
        string? expectedReadmeGitBlobSha1,
        NativeLifecycleReadySignal hostReady,
        NativeRenderCompleteSignal renderComplete,
        NativeFirstViewportImagesReadySignal ready)
    {
        if (ready.ProcessId != processId ||
            !string.Equals(ready.Host, expectedHost, StringComparison.Ordinal) ||
            ready.Generation != renderComplete.Generation ||
            ready.ViewportPaintGeneration != ready.Generation ||
            ready.PollCount <= 0 ||
            !double.IsFinite(ready.ProbeWorkMilliseconds) ||
            ready.ProbeWorkMilliseconds < 0 ||
            !double.IsFinite(ready.ViewportTop) ||
            ready.ViewportTop < 0 ||
            !double.IsFinite(ready.ViewportHeight) ||
            ready.ViewportHeight <= 0 ||
            !ready.ViewportMeasured ||
            ready.HasVisibleLoadingImages ||
            ready.Timestamp < renderComplete.Timestamp ||
            ready.Timestamp < hostReady.Timestamp ||
            !string.Equals(ready.ReadmeGitBlobSha1, expectedReadmeGitBlobSha1, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The native first-viewport readiness signal did not match the launched process, host, render generation, README identity, timestamp, or no-loading state.");
        }
    }

    private static JsonElement ReadRequiredProperty(JsonElement root, string name, string path)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            throw new InvalidDataException(
                $"Native lifecycle signal '{path}' is missing required property '{name}'.");
        }

        return value;
    }

    private static int ReadRequiredInt32(JsonElement root, string name, string path)
    {
        JsonElement value = ReadRequiredProperty(root, name, path);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            throw new InvalidDataException($"Native lifecycle signal '{path}' has an invalid '{name}' value.");
        return result;
    }

    private static long ReadRequiredInt64(JsonElement root, string name, string path)
    {
        JsonElement value = ReadRequiredProperty(root, name, path);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result))
            throw new InvalidDataException($"Native lifecycle signal '{path}' has an invalid '{name}' value.");
        return result;
    }

    private static double ReadRequiredDouble(JsonElement root, string name, string path)
    {
        JsonElement value = ReadRequiredProperty(root, name, path);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) || !double.IsFinite(result))
            throw new InvalidDataException($"Native lifecycle signal '{path}' has an invalid '{name}' value.");
        return result;
    }

    private static string ReadRequiredString(JsonElement root, string name, string path)
    {
        JsonElement value = ReadRequiredProperty(root, name, path);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Native lifecycle signal '{path}' has an invalid '{name}' value.");
        return value.GetString()!;
    }

    private static DateTimeOffset ReadRequiredTimestamp(JsonElement root, string name, string path)
    {
        JsonElement value = ReadRequiredProperty(root, name, path);
        if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out DateTimeOffset timestamp))
            throw new InvalidDataException($"Native lifecycle signal '{path}' has an invalid '{name}' value.");
        return timestamp;
    }
}

internal sealed record NativeLifecycleReadySignal(
    int ProcessId,
    string Stage,
    DateTimeOffset Timestamp);

internal sealed record NativeRenderCompleteSignal(
    int ProcessId,
    string Host,
    long Generation,
    DateTimeOffset Timestamp);

internal sealed record NativeFirstViewportImagesReadySignal(
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
