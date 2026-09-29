using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

internal static class NativeFirstViewportImagesReadyContract
{
    private const double InitialViewportTopToleranceDip = 0.5;

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
        List<string> mismatches = GetImagesReadySignalMismatches(
            processId,
            expectedHost,
            expectedReadmeGitBlobSha1,
            hostReady,
            renderComplete,
            ready);
        if (mismatches.Count > 0)
        {
            // Include field names only. Signal values can contain local process,
            // host, or repository identity details and are deliberately omitted.
            throw new InvalidDataException(
                "The native first-viewport readiness signal failed these checks: " +
                string.Join(", ", mismatches) + ".");
        }
    }

    internal static void ValidateInitialViewportIdentity(
        NativeFirstViewportImagesReadySignal ready,
        double capturedInitialDocumentTop)
    {
        List<string> mismatches = new(3);
        bool readyTopValid = double.IsFinite(ready.ViewportTop) && ready.ViewportTop >= 0;
        bool capturedTopValid = double.IsFinite(capturedInitialDocumentTop) && capturedInitialDocumentTop >= 0;
        if (!readyTopValid)
            mismatches.Add(nameof(ready.ViewportTop));
        if (!capturedTopValid)
            mismatches.Add(nameof(capturedInitialDocumentTop));

        if (readyTopValid && capturedTopValid &&
            Math.Abs(ready.ViewportTop - capturedInitialDocumentTop) > InitialViewportTopToleranceDip)
        {
            mismatches.Add("ViewportTopMatchesInitialCapture");
        }

        if (capturedTopValid && capturedInitialDocumentTop > InitialViewportTopToleranceDip)
            mismatches.Add("InitialCaptureStartsAtDocumentTop");

        if (mismatches.Count > 0)
        {
            // This is geometric audit state, but keep diagnostics field-only
            // so the validation surface never grows to include page content.
            throw new InvalidDataException(
                "The native first-viewport readiness signal did not identify the initial document-top capture: " +
                string.Join(", ", mismatches) + ".");
        }
    }

    private static List<string> GetImagesReadySignalMismatches(
        int processId,
        string expectedHost,
        string? expectedReadmeGitBlobSha1,
        NativeLifecycleReadySignal hostReady,
        NativeRenderCompleteSignal renderComplete,
        NativeFirstViewportImagesReadySignal ready)
    {
        List<string> mismatches = new(13);
        if (ready.ProcessId != processId)
            mismatches.Add(nameof(ready.ProcessId));
        if (!string.Equals(ready.Host, expectedHost, StringComparison.Ordinal))
            mismatches.Add(nameof(ready.Host));
        if (ready.Generation != renderComplete.Generation)
            mismatches.Add(nameof(ready.Generation));
        if (ready.ViewportPaintGeneration != ready.Generation)
            mismatches.Add(nameof(ready.ViewportPaintGeneration));
        if (ready.PollCount <= 0)
            mismatches.Add(nameof(ready.PollCount));
        if (!double.IsFinite(ready.ProbeWorkMilliseconds) || ready.ProbeWorkMilliseconds < 0)
            mismatches.Add(nameof(ready.ProbeWorkMilliseconds));
        if (!double.IsFinite(ready.ViewportTop) || ready.ViewportTop < 0)
            mismatches.Add(nameof(ready.ViewportTop));
        if (!double.IsFinite(ready.ViewportHeight) || ready.ViewportHeight <= 0)
            mismatches.Add(nameof(ready.ViewportHeight));
        if (!ready.ViewportMeasured)
            mismatches.Add(nameof(ready.ViewportMeasured));
        if (ready.HasVisibleLoadingImages)
            mismatches.Add(nameof(ready.HasVisibleLoadingImages));
        if (ready.Timestamp < renderComplete.Timestamp)
            mismatches.Add("TimestampAfterRenderComplete");
        if (ready.Timestamp < hostReady.Timestamp)
            mismatches.Add("TimestampAfterHostReady");
        if (!string.Equals(
                ready.ReadmeGitBlobSha1,
                expectedReadmeGitBlobSha1,
                StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(nameof(ready.ReadmeGitBlobSha1));
        }

        return mismatches;
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
