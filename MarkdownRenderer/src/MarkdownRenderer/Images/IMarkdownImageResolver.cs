using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Images;

/// <summary>
/// Resolves markdown image sources that need host-specific behavior, such as
/// authenticated repository assets. Return <see cref="MarkdownImageResolution.NotHandled"/>
/// only when the renderer may use its built-in URI loader.
/// </summary>
public interface IMarkdownImageResolver
{
    /// <summary>
    /// Resolves an image source and explicitly reports whether the host owns it.
    /// </summary>
    ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken);
}
