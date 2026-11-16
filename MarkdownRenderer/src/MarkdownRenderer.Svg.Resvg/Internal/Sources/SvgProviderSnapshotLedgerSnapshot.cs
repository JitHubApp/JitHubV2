namespace MarkdownRenderer.Svg.Resvg.Internal.Sources;

/// <summary>A coherent, content-free view of one provider snapshot ledger.</summary>
internal readonly record struct SvgProviderSnapshotLedgerSnapshot(
    long LiveBytes,
    long PeakBytes,
    long LiveSnapshots,
    long PeakSnapshots,
    long Sequence);
