using System;
using System.IO;
using System.Text.Json;

internal static class NativeReadinessProgressDiagnostic
{
    private const int MaximumTailBytes = 32 * 1024;
    private const int MaximumRecordBytes = 4096;

    internal static string ReadLastSummary(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "readinessProgress=unavailable";

        // Separate files/workers ensure optional publication diagnostics are
        // never part of the final ready-progress drain or signal barrier.
        string progress = ReadFileSummary(path);
        string publication = ReadFileSummary(GetPublicationOutcomePath(path));
        return publication.StartsWith("readinessPublication outcome=", StringComparison.Ordinal)
            ? progress + "; " + publication : progress;
    }

    internal static string GetPublicationOutcomePath(string progressPath) =>
        progressPath + ".publication.ndjson";

    internal static bool TryPreservePublicationOutcomes(string source, string destination)
    {
        try
        {
            return EvidenceFileSnapshot.PreserveIfPresent(source, destination, TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Optional observations must not fail an otherwise qualified audit
            // or mask its original failure. Required signal/evidence snapshots
            // still use the strict preservation path in ReadmeAuditProbe.
            return false;
        }
    }

    private static string ReadFileSummary(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "readinessProgress=unavailable";

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            long snapshotLength = stream.Length;
            long start = Math.Max(0, snapshotLength - MaximumTailBytes);
            stream.Position = start;
            int tailLength = checked((int)Math.Min(snapshotLength - start, MaximumTailBytes));
            byte[] tail = new byte[tailLength];
            int read = 0;
            while (read < tail.Length)
            {
                int count = stream.Read(tail, read, tail.Length - read);
                if (count == 0)
                    break;
                read += count;
            }

            return FormatLastSummary(tail.AsSpan(0, read), startsAtFileBeginning: start == 0);
        }
        catch (Exception)
        {
            // Diagnostics must not replace the real timeout/app-exit failure,
            // including malformed path configuration or malformed tail data.
            return "readinessProgress=unavailable";
        }
    }

    internal static string FormatLastSummary(ReadOnlySpan<byte> tail, bool startsAtFileBeginning)
    {
        if (tail.IsEmpty)
            return "readinessProgress=no-complete-record";

        int lineEndExclusive = tail.Length;
        if (tail[^1] == (byte)'\n')
        {
            lineEndExclusive--;
        }
        else
        {
            int lastNewline = tail.LastIndexOf((byte)'\n');
            if (lastNewline < 0)
                return "readinessProgress=no-complete-record";
            lineEndExclusive = lastNewline;
        }

        string? progressSummary = null;
        string? publicationSummary = null;
        while (lineEndExclusive > 0)
        {
            ReadOnlySpan<byte> prefix = tail[..lineEndExclusive];
            int previousNewline = prefix.LastIndexOf((byte)'\n');
            int lineStart = previousNewline + 1;
            ReadOnlySpan<byte> line = tail.Slice(lineStart, lineEndExclusive - lineStart);

            // If the bounded tail starts in the middle of a record, ignore
            // that partial prefix and continue only with complete later rows.
            bool completeRecord = startsAtFileBeginning || lineStart > 0;
            if (completeRecord && line.Length is > 0 and <= MaximumRecordBytes)
            {
                if (publicationSummary is null && TryFormatPublicationLine(line, out string? publication))
                    publicationSummary = publication;
                else if (progressSummary is null && TryFormatLine(line, out string? progress))
                    progressSummary = progress;
                if (publicationSummary is not null && progressSummary is not null)
                    break;
            }

            if (previousNewline < 0)
                break;
            lineEndExclusive = previousNewline;
        }

        // Publication rows are observations only: an earlier write failure can
        // precede a successful same-generation pending write. Never accept or
        // reject readiness from them; the signal and existing deadline govern.
        return progressSummary is not null
            ? progressSummary + (publicationSummary is null ? string.Empty : "; " + publicationSummary)
            : publicationSummary ?? "readinessProgress=no-valid-complete-record";
    }

    private static bool TryFormatPublicationLine(ReadOnlySpan<byte> line, out string? summary)
    {
        summary = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = 4,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("RecordKind", out JsonElement kind) ||
                kind.ValueKind != JsonValueKind.String || kind.GetString() != "readiness-publication-outcome" ||
                !root.TryGetProperty("SchemaVersion", out JsonElement schema) ||
                !schema.TryGetInt32(out int version) || version != 1 ||
                !TryReadAllowedString(root, "Outcome", IsAllowedPublicationOutcome, out string outcome) ||
                !root.TryGetProperty("ProcessId", out JsonElement process) ||
                !process.TryGetInt32(out int processId) || processId <= 0 ||
                !root.TryGetProperty("Generation", out JsonElement generation) ||
                !generation.TryGetInt64(out long generationId) || generationId <= 0 ||
                !root.TryGetProperty("ViewportPaintGeneration", out JsonElement paint) ||
                !paint.TryGetInt64(out long paintGeneration) || paintGeneration != generationId)
                return false;

            summary = $"readinessPublication outcome={outcome} processId={processId}" +
                $" generation={generationId} viewportPaintGeneration={paintGeneration}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsAllowedPublicationOutcome(string value) => value is
        "published" or "write-failed" or "faulted" or "canceled" or "suppressed";

    private static bool TryFormatLine(ReadOnlySpan<byte> line, out string? summary)
    {
        summary = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = 4,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("RecordKind", out _))
                return false;

            string stage = TryReadAllowedString(root, "Stage", IsAllowedStage, out string safeStage)
                ? safeStage
                : "unknown-token";
            string reason = TryReadAllowedString(root, "Reason", IsAllowedReason, out string safeReason)
                ? safeReason
                : "unknown-token";

            summary =
                $"readinessProgress stage={stage} reason={reason}" +
                $" generation={ReadNonNegativeInt64(root, "Generation")}" +
                $" currentGeneration={ReadNonNegativeInt64(root, "CurrentGeneration")}" +
                $" publishedGeneration={ReadNonNegativeInt64(root, "PublishedGeneration")}" +
                $" snapshotGeneration={ReadNonNegativeInt64(root, "SnapshotGeneration")}" +
                $" layoutRevision={ReadNonNegativeInt64(root, "LayoutRevision")}" +
                $" paintCallbacks={ReadNonNegativeInt64(root, "PaintCallbackCount")}" +
                $" paintedRegions={ReadNonNegativeInt64(root, "PaintedRegionCount")}" +
                $" paintRequestedGeneration={ReadNonNegativeInt64(root, "PaintRequestedGeneration")}" +
                $" paintedGeneration={ReadNonNegativeInt64(root, "PaintedGeneration")}" +
                $" visibleLoadingImages={ReadNonNegativeInt64(root, "VisibleLoadingImageCount")}" +
                $" pendingImageRegions={ReadNonNegativeInt64(root, "PendingImageRegionCount")}" +
                $" uncoveredRegions={ReadNonNegativeInt64(root, "UncoveredRegionCount")}" +
                $" viewportMeasured={ReadBoolean(root, "ViewportMeasured")}" +
                $" hasVisibleLoadingImages={ReadBoolean(root, "HasVisibleLoadingImages")}" +
                $" paintAcknowledgementRecorded={ReadBoolean(root, "PaintAcknowledgementRecorded")}" +
                $" paintAcknowledgementIsCurrent={ReadBoolean(root, "PaintAcknowledgementIsCurrent")}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadAllowedString(
        JsonElement root,
        string property,
        Func<string, bool> isAllowed,
        out string value)
    {
        value = string.Empty;
        if (root.TryGetProperty(property, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String &&
            element.GetString() is { } candidate &&
            isAllowed(candidate))
        {
            value = candidate;
            return true;
        }

        return false;
    }

    private static string ReadNonNegativeInt64(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out JsonElement element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt64(out long value) &&
            value >= 0)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return "n/a";
    }

    private static string ReadBoolean(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out JsonElement element) &&
            element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean() ? "true" : "false";
        }

        return "n/a";
    }

    private static bool IsAllowedStage(string value) => value is
        "armed" or "polling" or "paint-callback" or "ready" or "stopped";

    private static bool IsAllowedReason(string value) => value is
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
