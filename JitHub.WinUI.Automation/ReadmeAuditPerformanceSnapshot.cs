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
    public required ReadmeAuditPipelineTimingSnapshot Pipeline { get; init; }

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
            snapshot.PendingSourceByteRequests < 0 ||
            snapshot.Pipeline is null ||
            snapshot.Pipeline.Generation <= 0 ||
            snapshot.Pipeline.SourceUtf16Bytes < 0 ||
            !IsValidStageDuration(snapshot.Pipeline.ParseMilliseconds) ||
            !IsValidStageDuration(snapshot.Pipeline.SetupMilliseconds) ||
            !IsValidStageDuration(snapshot.Pipeline.LayoutMilliseconds) ||
            !IsValidStageDuration(snapshot.Pipeline.PublicationMilliseconds))
        {
            throw new InvalidDataException("The Markdown performance counters or presentation stages are invalid.");
        }

        return snapshot;
    }

    private static bool IsValidStageDuration(double duration) =>
        double.IsFinite(duration) && duration >= 0;
}

internal sealed class ReadmeAuditPipelineTimingSnapshot
{
    public required long Generation { get; init; }
    public required long SourceUtf16Bytes { get; init; }
    public required double ParseMilliseconds { get; init; }
    public required double SetupMilliseconds { get; init; }
    public required double LayoutMilliseconds { get; init; }
    public required double PublicationMilliseconds { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ReadmeAuditPerformanceSnapshot))]
[JsonSerializable(typeof(ReadmeAuditPipelineTimingSnapshot))]
internal partial class ReadmeAuditPerformanceJsonContext : JsonSerializerContext
{
}
