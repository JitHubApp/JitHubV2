namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// How <see cref="RasterNativeWicDecoder"/> copies converted pixels and where
/// it reports decode progress. The default copies the whole output with one
/// CopyPixels call and reports nothing.
/// </summary>
/// <param name="Diagnostics">Counts decodes for the owning image resource scope, if any.</param>
/// <param name="CopyBandRows">
/// Zero copies the whole output at once. A positive value replaces only the
/// final converter's CopyPixels call with bounded requests of at most this
/// many output rows; the decoded pixels are identical.
/// </param>
internal readonly record struct RasterNativeWicDecodeOptions(
    RasterNativeWicDiagnostics? Diagnostics = null,
    int CopyBandRows = 0)
{
    /// <summary>The largest supported <see cref="CopyBandRows"/>.</summary>
    internal const int MaximumCopyBandRows = 1_024;
}
