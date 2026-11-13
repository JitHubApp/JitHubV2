using System;
using System.Collections.Generic;
using System.Linq;

namespace JitHub.Services;

/// <summary>
/// Picks the branch a repository page selects while its branch list loads page by page.
/// </summary>
/// <remarks>
/// GitHub lists branches by name, so a repository with more than one page of
/// branches may not have its default branch in the first page. Flutter's
/// <c>master</c> comes after a hundred release branches such as <c>3.29</c>.
/// A branch the page already knows it wants is selected by name until its page
/// arrives, never replaced by whichever branch happened to load first.
/// </remarks>
public static class RepositoryBranchSelectionPolicy
{
    private static readonly string[] ConventionalDefaultBranches = ["main", "master"];

    /// <summary>Chooses the branch to select.</summary>
    /// <param name="loadedBranches">The branch names loaded so far, in list order.</param>
    /// <param name="knownBranch">
    /// The branch the page shows when no preferred branch is loaded, such as the
    /// repository's default branch, or <see langword="null"/> when it isn't known.
    /// </param>
    /// <param name="comparer">How branch names compare.</param>
    /// <param name="preferredBranches">Branches to select when loaded, most preferred first.</param>
    /// <returns>
    /// A preferred branch that is loaded; otherwise <paramref name="knownBranch"/>,
    /// even when it isn't loaded yet; otherwise a loaded <c>main</c> or <c>master</c>,
    /// or the first loaded branch. <see langword="null"/> when nothing is known or loaded.
    /// </returns>
    public static string? Resolve(
        IReadOnlyCollection<string> loadedBranches,
        string? knownBranch,
        StringComparer comparer,
        params string?[] preferredBranches)
    {
        ArgumentNullException.ThrowIfNull(loadedBranches);
        ArgumentNullException.ThrowIfNull(comparer);
        ArgumentNullException.ThrowIfNull(preferredBranches);

        foreach (string? preferred in preferredBranches)
        {
            if (!string.IsNullOrWhiteSpace(preferred) && loadedBranches.Contains(preferred, comparer))
            {
                return preferred;
            }
        }

        if (!string.IsNullOrWhiteSpace(knownBranch))
        {
            return knownBranch;
        }

        foreach (string conventional in ConventionalDefaultBranches)
        {
            if (loadedBranches.Contains(conventional, comparer))
            {
                return conventional;
            }
        }

        return loadedBranches.FirstOrDefault();
    }
}
