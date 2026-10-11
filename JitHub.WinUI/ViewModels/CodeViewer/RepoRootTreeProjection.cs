using System.Collections.Generic;
using System.Linq;
using JitHub.Models.CodeViewer;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.Services.Markdown;

namespace JitHub.WinUI.ViewModels.CodeViewer;

internal static class RepoRootTreeProjection
{
    public static RepoCodeLoadResult<RepoTree> Create(
        RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>> root)
    {
        // Offline same-byte replay deliberately supplies one manifest-pinned file,
        // not a live listing of the repository root. A fresh pinned projection is
        // still partial and cannot establish that every root child is represented.
        bool rootIsAuthoritative = root.CacheState == CacheState.Fresh &&
            !MarkdownLifecycleAutomationBridge.IsOfflineSameBytePublicAuditEnabled;
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
