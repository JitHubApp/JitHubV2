using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Fail-closed audit view of the production Markdown performance evidence.
/// This is intentionally shared with contract tests so newly emitted counters
/// cannot disappear silently during JSON deserialization.
/// </summary>
internal sealed class ReadmeAuditPerformanceSnapshot
{
    private const long X64SourceBudgetBytes = 64L * 1024 * 1024;

    public required long SourceCacheBytes { get; init; }
    public required long SourceCacheHits { get; init; }
    public required long ImageFetches { get; init; }
    public required long ImageFetchMilliseconds { get; init; }
    public required long ImageFetchFailures { get; init; }
    public required long ImageFetchCancellations { get; init; }
    public required long SourceCacheEvictions { get; init; }
    public required int PendingImageFetches { get; init; }
    public required int ActiveImageFetches { get; init; }
    public required long CpuPreparations { get; init; }
    public required long CpuPreparationMilliseconds { get; init; }
    public required long ScenePreparations { get; init; }
    public required long ScenePreparationMilliseconds { get; init; }
    public required long InFlightSourceBytes { get; init; }
    public required long PeakInFlightSourceBytes { get; init; }
    public required int PendingSourceByteRequests { get; init; }

    internal static ReadmeAuditPerformanceSnapshot Parse(JsonElement evidence)
    {
        if (evidence.ValueKind != JsonValueKind.Object ||
            !evidence.TryGetProperty("Performance", out JsonElement performance) ||
            performance.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The Markdown performance evidence is missing.");
        }

        ReadmeAuditPerformanceSnapshot snapshot;
        try
        {
            snapshot = performance.Deserialize(
                    ReadmeAuditPerformanceJsonContext.Default.ReadmeAuditPerformanceSnapshot)
                ?? throw new InvalidDataException("The Markdown performance evidence is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Markdown performance evidence is incomplete.", exception);
        }

        if (snapshot.SourceCacheBytes is < 0 or > X64SourceBudgetBytes ||
            snapshot.SourceCacheHits < 0 || snapshot.ImageFetches < 0 ||
            snapshot.ImageFetchMilliseconds < 0 || snapshot.ImageFetchFailures < 0 ||
            snapshot.ImageFetchCancellations < 0 || snapshot.SourceCacheEvictions < 0 ||
            snapshot.PendingImageFetches < 0 || snapshot.ActiveImageFetches < 0 ||
            snapshot.CpuPreparations < 0 || snapshot.CpuPreparationMilliseconds < 0 ||
            snapshot.ScenePreparations < 0 || snapshot.ScenePreparationMilliseconds < 0 ||
            snapshot.InFlightSourceBytes < 0 ||
            snapshot.PeakInFlightSourceBytes < snapshot.InFlightSourceBytes ||
            snapshot.PeakInFlightSourceBytes > X64SourceBudgetBytes ||
            snapshot.PendingSourceByteRequests < 0)
        {
            throw new InvalidDataException("The Markdown performance counters exceed their bounds.");
        }

        return snapshot;
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ReadmeAuditPerformanceSnapshot))]
internal partial class ReadmeAuditPerformanceJsonContext : JsonSerializerContext
{
}
