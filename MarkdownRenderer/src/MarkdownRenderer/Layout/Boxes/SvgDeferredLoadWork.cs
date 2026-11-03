using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The deferral, demand, load generation and denial identity a deferred SVG host
/// load resumes with.
/// </summary>
internal readonly struct SvgDeferredLoadWork(
    SvgHostLoadDeferral owner,
    MarkdownSvgRenderDemand demand,
    int loadGeneration,
    object? denialIdentity)
{
    internal readonly SvgHostLoadDeferral Owner = owner;
    internal readonly MarkdownSvgRenderDemand Demand = demand;
    internal readonly int LoadGeneration = loadGeneration;
    internal readonly object? DenialIdentity = denialIdentity;
}
