using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The box, demand, load generation and denial identity a deferred SVG host
/// load resumes with.
/// </summary>
internal readonly struct SvgDeferredLoadWork(
    ImageBox owner,
    MarkdownSvgRenderDemand demand,
    int loadGeneration,
    object? denialIdentity)
{
    internal readonly ImageBox Owner = owner;
    internal readonly MarkdownSvgRenderDemand Demand = demand;
    internal readonly int LoadGeneration = loadGeneration;
    internal readonly object? DenialIdentity = denialIdentity;
}
