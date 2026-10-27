using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;

namespace JitHub.Services.Markdown;

/// <summary>
/// Adds the leased-resolution capability only when the configured GitHub image service
/// can provide it, while forwarding all compatibility operations to the existing resolver.
/// </summary>
internal sealed class LeasedGitHubMarkdownImageResolver :
    IMarkdownLeasedImageResolver,
    IMarkdownImageSourceByteAdmittedResolver,
    IMarkdownImagePrefetcher
{
    private readonly IMarkdownImageResolver _legacyResolver;
    private readonly Func<
        string,
        MarkdownImageResolveContext,
        IMarkdownImageSourceByteAdmission,
        CancellationToken,
        ValueTask<MarkdownImageResolutionLease>> _resolveWithSourceLease;

    internal LeasedGitHubMarkdownImageResolver(
        IMarkdownImageResolver legacyResolver,
        Func<
            string,
            MarkdownImageResolveContext,
            IMarkdownImageSourceByteAdmission,
            CancellationToken,
            ValueTask<MarkdownImageResolutionLease>> resolveWithSourceLease)
    {
        _legacyResolver = legacyResolver ?? throw new ArgumentNullException(nameof(legacyResolver));
        _resolveWithSourceLease = resolveWithSourceLease ??
            throw new ArgumentNullException(nameof(resolveWithSourceLease));
    }

    public ValueTask PrefetchAsync(
        IReadOnlyList<string> sources,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken) =>
        _legacyResolver is IMarkdownImagePrefetcher prefetcher
            ? prefetcher.PrefetchAsync(sources, context, cancellationToken)
            : ValueTask.CompletedTask;

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken) =>
        _legacyResolver.ResolveAsync(source, context, cancellationToken);

    public ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return _legacyResolver is IMarkdownImageSourceByteAdmittedResolver admitted
            ? admitted.ResolveWithSourceByteAdmissionAsync(source, context, admission, cancellationToken)
            : _legacyResolver.ResolveAsync(source, context, cancellationToken);
    }

    public ValueTask<MarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return _resolveWithSourceLease(source, context, admission, cancellationToken);
    }
}
