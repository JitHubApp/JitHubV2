using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// What one <see cref="ImageSourceLoader"/> resolver load holds while it runs:
/// the request, the resolver task until it is tracked, and the resolved
/// source's aliases and caller-owned lease. The loader clears the aliases and
/// disposes the lease in one place. It is a class because the asynchronous
/// stages that fill it cannot take a <see langword="ref"/>.
/// </summary>
internal sealed class ImageResolveState
{
    internal ImageResolveState(
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext resolveContext,
        MarkdownSvgRenderDemand demand,
        int loadDemandGeneration,
        CancellationToken lifetimeToken,
        BitmapOutputWorkContext bitmapOutputWork)
    {
        Resolver = resolver;
        ResolveContext = resolveContext;
        Demand = demand;
        LoadDemandGeneration = loadDemandGeneration;
        LifetimeToken = lifetimeToken;
        BitmapOutputWork = bitmapOutputWork;
    }

    internal readonly IMarkdownImageResolver Resolver;
    internal readonly MarkdownImageResolveContext ResolveContext;
    internal readonly MarkdownSvgRenderDemand Demand;
    internal readonly int LoadDemandGeneration;
    internal readonly CancellationToken LifetimeToken;
    internal readonly BitmapOutputWorkContext BitmapOutputWork;

    // The started resolver operation, until detached tracking owns it.
    internal Task? ResolverTask;

    // Source-bearing aliases and the caller-owned source lease.
    internal MarkdownImageResolution Resolution;
    internal MarkdownImageAsset? Asset;
    internal IMarkdownImageResolutionLease? SourceLease;
    internal IMarkdownImageResolutionLease? ConsumerSourceLease;
}
