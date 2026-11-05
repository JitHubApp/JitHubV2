using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Markdown;

/// <summary>
/// Fail-closed image resolver used when the authenticated resolver cannot be resolved.
/// It deliberately owns every source so the renderer cannot fall back to URI loading.
/// </summary>
public sealed class DenyAllMarkdownImageResolver : IMarkdownImageResolver
{
    public static DenyAllMarkdownImageResolver Instance { get; } = new();

    private DenyAllMarkdownImageResolver()
    {
    }

    /// <summary>
    /// Creates the host's image resolver, failing closed to <see cref="Instance"/>
    /// when the provider is missing, returns nothing or throws.
    /// </summary>
    /// <param name="createResolver">Resolves the provider and creates its resolver.</param>
    public static IMarkdownImageResolver CreateOrDenyAll(Func<IMarkdownImageResolver?> createResolver)
    {
        ArgumentNullException.ThrowIfNull(createResolver);
        try
        {
            return createResolver() ?? Instance;
        }
        catch (Exception)
        {
            // A broken resolver registration must never fall back to URI loading.
            return Instance;
        }
    }

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MarkdownImageUnavailableReason reason = Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) &&
            uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                ? MarkdownImageUnavailableReason.InsecureRemoteContent
                : MarkdownImageUnavailableReason.RemoteContentBlocked;
        return ValueTask.FromResult(MarkdownImageResolution.Blocked(reason));
    }
}
