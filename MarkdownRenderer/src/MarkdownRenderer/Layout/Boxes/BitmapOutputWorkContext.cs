using System.Threading;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The load demand, generations and cancellation one image work item captured
/// when it began, so its bitmap output can be admitted only while that exact
/// request is still the box's current one.
/// </summary>
internal readonly struct BitmapOutputWorkContext(
    MarkdownSvgRenderDemand? Demand,
    int LoadGeneration,
    int SvgGeneration,
    CancellationToken CancellationToken,
    bool IsExistingSvgTileOutput,
    bool IsValid)
{
    public MarkdownSvgRenderDemand? Demand { get; init; } = Demand;
    public int LoadGeneration { get; init; } = LoadGeneration;
    public int SvgGeneration { get; init; } = SvgGeneration;
    public CancellationToken CancellationToken { get; init; } = CancellationToken;
    public bool IsExistingSvgTileOutput { get; init; } = IsExistingSvgTileOutput;
    public bool IsValid { get; init; } = IsValid;

    /// <summary>True while <paramref name="host"/> still holds the captured request.</summary>
    internal bool IsCurrent(IImageBitmapOutputHost host) =>
        IsValid &&
        !CancellationToken.IsCancellationRequested &&
        ReferenceEquals(host.LoadDemand, Demand) &&
        host.LoadDemandGeneration == LoadGeneration &&
        host.SvgRenderGeneration == SvgGeneration;
}
