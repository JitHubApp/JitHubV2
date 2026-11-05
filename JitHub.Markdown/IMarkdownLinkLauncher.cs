using System;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Markdown;

/// <summary>Opens a Markdown link outside the app, in the reader's default handler.</summary>
public interface IMarkdownLinkLauncher
{
    /// <summary>Launches <paramref name="uri"/>.</summary>
    /// <param name="uri">The resolved link target.</param>
    /// <param name="cancellationToken">Cancels the launch.</param>
    /// <returns><see langword="true"/> when the system accepted the launch.</returns>
    Task<bool> LaunchAsync(Uri uri, CancellationToken cancellationToken = default);
}
