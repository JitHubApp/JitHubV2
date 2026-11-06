using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Markdown.Images;

/// <summary>
/// The authenticated GitHub REST reads that Markdown image resolution needs:
/// the server-rendered README for Camo maps, and repository files and blobs
/// for repository images. The app adapts its GitHub client to this contract.
/// </summary>
public interface IGitHubMarkdownContentClient
{
    /// <summary>Gets GitHub's server-rendered README HTML for a repository.</summary>
    Task<string> GetRenderedReadmeHtmlAsync(
        string token,
        string owner,
        string name,
        string? gitRef,
        CancellationToken cancellationToken);

    /// <summary>Gets one repository file through the Contents API.</summary>
    Task<GitHubMarkdownEncodedContent> GetRepositoryFileAsync(
        string token,
        string owner,
        string name,
        string path,
        string? gitRef,
        CancellationToken cancellationToken);

    /// <summary>Gets one Git blob through the Blobs API.</summary>
    Task<GitHubMarkdownEncodedContent> GetBlobAsync(
        string token,
        string owner,
        string name,
        string sha,
        CancellationToken cancellationToken);
}
