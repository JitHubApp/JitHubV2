namespace MarkdownRenderer.Controls;

/// <summary>
/// Captures the committed UI publication interval required by schema-12
/// release measurements. This reference-only shape intentionally contains no
/// runtime telemetry beyond the timestamps consumed by the harness.
/// </summary>
internal readonly struct MarkdownPipelineTimingSnapshot
{
    internal readonly long Generation;
    internal readonly long SourceUtf16Bytes;
    internal readonly long PublicationStartedTimestamp;
    internal readonly long PublicationEndedTimestamp;

    internal MarkdownPipelineTimingSnapshot(
        long generation,
        long sourceUtf16Bytes,
        long publicationStartedTimestamp,
        long publicationEndedTimestamp)
    {
        Generation = generation;
        SourceUtf16Bytes = sourceUtf16Bytes;
        PublicationStartedTimestamp = publicationStartedTimestamp;
        PublicationEndedTimestamp = publicationEndedTimestamp;
    }
}
