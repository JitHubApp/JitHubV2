using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown.Images.Caching;
using MarkdownRenderer.Images;

namespace JitHub.Markdown.Images.Repository;

/// <summary>
/// Optional authenticated raw-Contents capability used by the leased image path.
/// The general GitHub client contract remains JSON-based for compatibility.
/// </summary>
public interface IGitHubRepositoryImageClient
{
    Task<GitHubImageDownload?> GetRepositoryImageContentAsync(
        string token,
        string owner,
        string name,
        string path,
        string? gitRef,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default);
}
