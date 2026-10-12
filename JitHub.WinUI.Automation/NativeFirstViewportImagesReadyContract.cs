using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

internal static class NativeFirstViewportImagesReadyContract
{
    internal const string ExpectedVisualProfileId = "github-primer-light";
    internal const int ExpectedVisualProfileVersion = 5;
    internal const string ExpectedVisualProfileCssSha256 =
        "15b6b573ceb5fa8f0e103a14d04d6c8b6ed7a5a0b456a71674a249d183374d81";

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
            ReadRequiredTimestamp(root, "Timestamp", path),
            ReadRequiredInt64(root, "StopwatchTicks", path),
            ReadRequiredInt64(root, "StopwatchFrequency", path),
            ReadOptionalSameByteReadmeSourceAdmission(root, path),
            ReadOptionalSameByteReadmeSourceAdmissionFailure(root, path),
            ReadOptionalVisualProfile(root, path));
    }

    // The audit route normally opens its README while the shell starts. A
    // source-bound render signal is sufficient to avoid a redundant UIA tree
    // query, but it is not a replacement for the later viewport-ready and
    // full-traversal validation.
    internal static bool MatchesAlreadyRenderedReadme(
        NativeRenderCompleteSignal render,
        int processId,
        string expectedHost,
        string expectedRepositoryFullName,
        string expectedRef,
        string expectedPath,
        string expectedReadmeGitBlobSha1,
        string expectedRawSha256,
        long expectedRawByteSize)
    {
        NativeVisualProfileSignal? visualProfile = render.VisualProfile;
        NativeSameByteReadmeSourceAdmissionSignal? source =
            render.SameByteReadmeSourceAdmission;
        return processId > 0 &&
            render.ProcessId == processId &&
            string.Equals(render.Host, expectedHost, StringComparison.Ordinal) &&
            render.Generation > 0 &&
            render.StopwatchTicks > 0 &&
            render.StopwatchFrequency > 0 &&
            visualProfile is not null &&
            string.Equals(visualProfile.Id, ExpectedVisualProfileId, StringComparison.Ordinal) &&
            visualProfile.Version == ExpectedVisualProfileVersion &&
            string.Equals(
                visualProfile.CssSha256,
                ExpectedVisualProfileCssSha256,
                StringComparison.OrdinalIgnoreCase) &&
            render.SameByteReadmeSourceAdmissionFailure is null &&
            source is not null &&
            source.ProcessId == processId &&
            string.Equals(source.Host, expectedHost, StringComparison.Ordinal) &&
            source.Generation == render.Generation &&
            source.StopwatchTicks > 0 &&
            source.StopwatchFrequency == render.StopwatchFrequency &&
            source.StopwatchTicks <= render.StopwatchTicks &&
            string.Equals(
                $"{source.SourceOwner}/{source.SourceRepository}",
                expectedRepositoryFullName,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(source.SourceRef, expectedRef, StringComparison.Ordinal) &&
            string.Equals(source.SourcePath, expectedPath, StringComparison.Ordinal) &&
            source.RawByteSize == expectedRawByteSize &&
            string.Equals(source.RawSha256, expectedRawSha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(source.ReadmeGitBlobSha1, expectedReadmeGitBlobSha1, StringComparison.OrdinalIgnoreCase);
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
            ReadRequiredTimestamp(root, "Timestamp", path),
            ReadRequiredInt64(root, "StopwatchTicks", path),
            ReadRequiredInt64(root, "StopwatchFrequency", path));
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
            renderComplete.StopwatchTicks <= 0 ||
            renderComplete.StopwatchFrequency <= 0 ||
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
        if (ready.StopwatchTicks <= 0)
            mismatches.Add(nameof(ready.StopwatchTicks));
        if (ready.StopwatchFrequency <= 0 ||
            ready.StopwatchFrequency != renderComplete.StopwatchFrequency)
        {
            mismatches.Add(nameof(ready.StopwatchFrequency));
        }
        if (ready.StopwatchTicks < renderComplete.StopwatchTicks)
            mismatches.Add("StopwatchTicksAfterRenderComplete");
        if (!string.Equals(
                ready.ReadmeGitBlobSha1,
                expectedReadmeGitBlobSha1,
                StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(nameof(ready.ReadmeGitBlobSha1));
        }

        return mismatches;
    }

    internal static double ValidateSameByteSourceTiming(
        int processId,
        string expectedHost,
        string expectedReadmeGitBlobSha1,
        int expectedRawByteSize,
        string expectedRawSha256,
        int expectedDecodedTextUtf8ByteSize,
        string expectedDecodedTextSha256,
        NativeRenderCompleteSignal renderComplete,
        NativeFirstViewportImagesReadySignal ready)
    {
        List<string> mismatches = new(14);
        NativeSameByteReadmeSourceAdmissionSignal? admission = renderComplete.SameByteReadmeSourceAdmission;
        if (admission is null)
        {
            mismatches.Add(nameof(renderComplete.SameByteReadmeSourceAdmission));
        }
        else
        {
            if (admission.ProcessId != processId)
                mismatches.Add(nameof(admission.ProcessId));
            if (!string.Equals(admission.Host, expectedHost, StringComparison.Ordinal))
                mismatches.Add(nameof(admission.Host));
            if (admission.Generation != renderComplete.Generation || admission.Generation != ready.Generation)
                mismatches.Add(nameof(admission.Generation));
            if (admission.RawByteSize != expectedRawByteSize)
                mismatches.Add(nameof(admission.RawByteSize));
            if (!string.Equals(admission.RawSha256, expectedRawSha256, StringComparison.OrdinalIgnoreCase))
                mismatches.Add(nameof(admission.RawSha256));
            if (admission.DecodedTextUtf8ByteSize != expectedDecodedTextUtf8ByteSize)
                mismatches.Add(nameof(admission.DecodedTextUtf8ByteSize));
            if (!string.Equals(admission.DecodedTextSha256, expectedDecodedTextSha256, StringComparison.OrdinalIgnoreCase))
                mismatches.Add(nameof(admission.DecodedTextSha256));
            if (!string.Equals(admission.ReadmeGitBlobSha1, expectedReadmeGitBlobSha1, StringComparison.OrdinalIgnoreCase))
                mismatches.Add(nameof(admission.ReadmeGitBlobSha1));
            if (admission.StopwatchTicks <= 0)
                mismatches.Add(nameof(admission.StopwatchTicks));
            if (admission.StopwatchFrequency <= 0 ||
                admission.StopwatchFrequency != renderComplete.StopwatchFrequency ||
                admission.StopwatchFrequency != ready.StopwatchFrequency)
            {
                mismatches.Add(nameof(admission.StopwatchFrequency));
            }
            if (admission.StopwatchTicks > renderComplete.StopwatchTicks)
                mismatches.Add("StopwatchTicksBeforeRenderComplete");
            if (renderComplete.StopwatchTicks > ready.StopwatchTicks)
                mismatches.Add("RenderCompleteBeforeReadyStopwatchTicks");
        }

        if (expectedRawByteSize <= 0 || !IsSha256(expectedRawSha256))
            mismatches.Add("ExpectedRawIdentity");
        if (expectedDecodedTextUtf8ByteSize < 0 || !IsSha256(expectedDecodedTextSha256))
            mismatches.Add("ExpectedDecodedTextIdentity");
        if (!IsGitSha1(expectedReadmeGitBlobSha1))
            mismatches.Add("ExpectedReadmeGitBlobSha1");

        if (mismatches.Count > 0)
        {
            throw new InvalidDataException(
                "The native same-byte source-admission timing failed these checks: " +
                string.Join(", ", mismatches) + ".");
        }

        long elapsedTicks = ready.StopwatchTicks - admission!.StopwatchTicks;
        double elapsedMilliseconds = elapsedTicks * 1000d / admission.StopwatchFrequency;
        if (elapsedTicks <= 0 || !double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds <= 0)
        {
            throw new InvalidDataException(
                "The native same-byte source-admission timing was not a finite positive monotonic interval.");
        }

        return elapsedMilliseconds;
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

    private static NativeSameByteReadmeSourceAdmissionSignal? ReadOptionalSameByteReadmeSourceAdmission(
        JsonElement root,
        string path)
    {
        if (!root.TryGetProperty("SameByteReadmeSourceAdmission", out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Native lifecycle signal '{path}' has an invalid same-byte source-admission object.");

        return new NativeSameByteReadmeSourceAdmissionSignal(
            ReadRequiredInt32(value, "ProcessId", path),
            ReadRequiredString(value, "Host", path),
            ReadRequiredInt64(value, "Generation", path),
            ReadRequiredInt32(value, "RawByteSize", path),
            ReadRequiredString(value, "RawSha256", path),
            ReadRequiredInt32(value, "DecodedTextUtf8ByteSize", path),
            ReadRequiredString(value, "DecodedTextSha256", path),
            ReadRequiredString(value, "ReadmeGitBlobSha1", path),
            ReadRequiredInt64(value, "StopwatchTicks", path),
            ReadRequiredInt64(value, "StopwatchFrequency", path),
            ReadOptionalString(value, "SourceOwner", path),
            ReadOptionalString(value, "SourceRepository", path),
            ReadOptionalString(value, "SourceRef", path),
            ReadOptionalString(value, "SourcePath", path));
    }

    private static NativeSameByteReadmeSourceAdmissionFailureSignal? ReadOptionalSameByteReadmeSourceAdmissionFailure(
        JsonElement root,
        string path)
    {
        if (!root.TryGetProperty("SameByteReadmeSourceAdmissionFailure", out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Native lifecycle signal '{path}' has an invalid same-byte source-admission failure object.");

        string status = ReadRequiredString(value, "Status", path);
        if (status is not (
            "audit-disabled" or
            "document-identity-missing" or
            "source-evidence-missing" or
            "git-blob-mismatch" or
            "decoded-text-size-mismatch" or
            "decoded-text-digest-mismatch" or
            "generation-not-advanced" or
            "assignment-generation-unavailable" or
            "assignment-generation-superseded" or
            "source-identity-changed" or
            "monotonic-clock-invalid" or
            "markdown-unchanged" or
            "mutable-source-ref" or
            "rendered-document-changed" or
            "admission-state-invalid"))
        {
            throw new InvalidDataException($"Native lifecycle signal '{path}' has an unknown same-byte source-admission failure status.");
        }

        return new NativeSameByteReadmeSourceAdmissionFailureSignal(
            ReadRequiredString(value, "Host", path),
            ReadRequiredInt64(value, "Generation", path),
            status);
    }

    private static int ReadRequiredInt32(JsonElement root, string name, string path)
    {
        JsonElement value = ReadRequiredProperty(root, name, path);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            throw new InvalidDataException($"Native lifecycle signal '{path}' has an invalid '{name}' value.");
        return result;
    }

    private static NativeVisualProfileSignal? ReadOptionalVisualProfile(
        JsonElement root,
        string path)
    {
        if (!root.TryGetProperty("VisualProfile", out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Native lifecycle signal '{path}' has an invalid visual profile object.");

        string id = ReadRequiredString(value, "Id", path);
        int version = ReadRequiredInt32(value, "Version", path);
        string cssSha256 = ReadRequiredString(value, "CssSha256", path);
        if (version < 1 || !IsSha256(cssSha256))
            throw new InvalidDataException($"Native lifecycle signal '{path}' has an invalid visual profile identity.");

        return new NativeVisualProfileSignal(id, version, cssSha256.ToLowerInvariant());
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

    private static string? ReadOptionalString(JsonElement root, string name, string path)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Native lifecycle signal '{path}' has an invalid '{name}' value.");
        return value.GetString();
    }

    private static DateTimeOffset ReadRequiredTimestamp(JsonElement root, string name, string path)
    {
        JsonElement value = ReadRequiredProperty(root, name, path);
        if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out DateTimeOffset timestamp))
            throw new InvalidDataException($"Native lifecycle signal '{path}' has an invalid '{name}' value.");
        return timestamp;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsGitSha1(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);
}

internal sealed record NativeLifecycleReadySignal(
    int ProcessId,
    string Stage,
    DateTimeOffset Timestamp);

internal sealed record NativeRenderCompleteSignal(
    int ProcessId,
    string Host,
    long Generation,
    DateTimeOffset Timestamp,
    long StopwatchTicks,
    long StopwatchFrequency,
    NativeSameByteReadmeSourceAdmissionSignal? SameByteReadmeSourceAdmission = null,
    NativeSameByteReadmeSourceAdmissionFailureSignal? SameByteReadmeSourceAdmissionFailure = null,
    NativeVisualProfileSignal? VisualProfile = null);

internal sealed record NativeVisualProfileSignal(
    string Id,
    int Version,
    string CssSha256);

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
    DateTimeOffset Timestamp,
    long StopwatchTicks,
    long StopwatchFrequency);

internal sealed record NativeSameByteReadmeSourceAdmissionSignal(
    int ProcessId,
    string Host,
    long Generation,
    int RawByteSize,
    string RawSha256,
    int DecodedTextUtf8ByteSize,
    string DecodedTextSha256,
    string ReadmeGitBlobSha1,
    long StopwatchTicks,
    long StopwatchFrequency,
    string? SourceOwner = null,
    string? SourceRepository = null,
    string? SourceRef = null,
    string? SourcePath = null);

internal sealed record NativeSameByteReadmeSourceAdmissionFailureSignal(
    string Host,
    long Generation,
    string Status);

internal static class NativeScrollTerminalContract
{
    private const double ScrollPercentTolerance = 0.01;
    private const double TerminalScrollPercent = 100 - 0.000001;

    internal static bool IsTerminalNoOpCandidate(
        double currentPercent,
        double settledPercent,
        double settledViewSize,
        bool verticallyScrollable)
    {
        return double.IsFinite(currentPercent) &&
            double.IsFinite(settledPercent) &&
            double.IsFinite(settledViewSize) &&
            settledViewSize > 0 && settledViewSize <= 100 &&
            Math.Abs(settledPercent - currentPercent) <= ScrollPercentTolerance &&
            HasTerminalEvidence(settledPercent, verticallyScrollable);
    }

    internal static bool IsStableTerminalConfirmation(
        double candidatePercent,
        double candidateViewSize,
        double currentPercent,
        double currentViewSize,
        bool verticallyScrollable)
    {
        return double.IsFinite(candidatePercent) &&
            double.IsFinite(candidateViewSize) &&
            double.IsFinite(currentPercent) &&
            double.IsFinite(currentViewSize) &&
            candidateViewSize > 0 && candidateViewSize <= 100 &&
            currentViewSize > 0 && currentViewSize <= 100 &&
            Math.Abs(currentPercent - candidatePercent) <= ScrollPercentTolerance &&
            Math.Abs(currentViewSize - candidateViewSize) <= ScrollPercentTolerance &&
            HasTerminalEvidence(currentPercent, verticallyScrollable);
    }

    private static bool HasTerminalEvidence(double percent, bool verticallyScrollable) =>
        percent >= TerminalScrollPercent || !verticallyScrollable;
}
