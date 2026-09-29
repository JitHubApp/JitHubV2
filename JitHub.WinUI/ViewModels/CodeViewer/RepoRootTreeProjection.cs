using System.Collections.Generic;
using System.Linq;
using JitHub.Models.CodeViewer;
using JitHub.Services;
using JitHub.Services.CodeViewer;

namespace JitHub.WinUI.ViewModels.CodeViewer;

internal static class RepoRootTreeProjection
{
    public static RepoCodeLoadResult<RepoTree> Create(
        RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>> root)
    {
        bool rootIsAuthoritative = root.CacheState == CacheState.Fresh;
        return new RepoCodeLoadResult<RepoTree>(
            new RepoTree
            {
                Truncated = true,
                RootIsAuthoritative = rootIsAuthoritative,
                Root = new RepoTreeNode
                {
                    Name = string.Empty,
                    Path = string.Empty,
                    IsDirectory = true,
                    Children = root.Value.ToList()
                }
            },
            root.CacheState,
            root.IsRefreshInProgress,
            root.RefreshError,
            root.FetchedAt,
            root.StaleAfter);
    }
}
