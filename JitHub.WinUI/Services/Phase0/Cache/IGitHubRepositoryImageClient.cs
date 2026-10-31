using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Services;

/// <summary>
/// Optional authenticated raw-Contents capability used by the leased image path.
/// The general GitHub client contract remains JSON-based for compatibility.
/// </summary>
internal interface IGitHubRepositoryImageClient
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
