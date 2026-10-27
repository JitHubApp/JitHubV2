namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// A host-only transient result when source normalization cannot reserve its
/// bounded scratch peak. Public provider callers translate this to the stable
/// resource-limit failure because that API has no deferred-admission result.
/// </summary>
internal sealed class SvgPreflightCapacityUnavailableException(
    long capacityVersion,
    long requiredSourceReservationBytes) : Exception("SVG normalization source capacity is temporarily unavailable.")
{
    internal long CapacityVersion { get; } = capacityVersion >= 0
        ? capacityVersion
        : throw new ArgumentOutOfRangeException(nameof(capacityVersion));

    internal long RequiredSourceReservationBytes { get; } = requiredSourceReservationBytes > 0
        ? requiredSourceReservationBytes
        : throw new ArgumentOutOfRangeException(nameof(requiredSourceReservationBytes));
}
