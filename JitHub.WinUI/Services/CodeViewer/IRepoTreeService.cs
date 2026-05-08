using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models;
using JitHub.Models.CodeViewer;

namespace JitHub.Services.CodeViewer;

public interface IRepoTreeService
{
    Task<RepoTree> LoadTreeAsync(string owner, string name, string refOrSha, CancellationToken ct);

    Task<IReadOnlyList<RepoContentNode>> LoadDirectoryAsync(
        string owner,
        string name,
        string path,
        string refOrSha,
        CancellationToken ct);

    Task<RepoFileBlob> LoadBlobAsync(string owner, string name, string sha, CancellationToken ct);
}
