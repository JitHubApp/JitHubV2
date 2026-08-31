using System;
using System.Collections.Generic;
using System.Linq;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public static class GistLibraryProjection
{
    public static GistLibraryProjectionSnapshot CreateSnapshot(
        IEnumerable<GitHubGist> gists,
        string search,
        GistVisibilityFilter filter,
        GistLibrarySort sort)
    {
        GitHubGist[] items = Sort(
                gists.Where(gist => Matches(gist, search, filter)),
                sort)
            .ToArray();
        return new GistLibraryProjectionSnapshot(
            items,
            items.Select(static gist => gist.Id).ToHashSet(StringComparer.Ordinal));
    }

    public static string GetTitle(GitHubGist gist)
    {
        if (!string.IsNullOrWhiteSpace(gist.Description))
        {
            return gist.Description.Trim();
        }

        return gist.Files.Values.FirstOrDefault()?.Filename is string filename && !string.IsNullOrWhiteSpace(filename)
            ? filename
            : "Untitled gist";
    }

    public static string GetFileSummary(GitHubGist gist)
    {
        if (gist.Files.Count == 0)
        {
            return "No files";
        }

        string first = gist.Files.Values.OrderBy(static file => file.Filename, StringComparer.OrdinalIgnoreCase).First().Filename;
        return gist.Files.Count == 1 ? first : $"{first} +{gist.Files.Count - 1}";
    }

    public static bool Matches(GitHubGist gist, string search, GistVisibilityFilter filter)
    {
        if (filter == GistVisibilityFilter.Public && !gist.Public
            || filter == GistVisibilityFilter.Secret && gist.Public)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        string term = search.Trim();
        return (gist.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || (gist.Owner?.Login?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || gist.Files.Values.Any(file =>
                file.Filename.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (file.Language?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    public static IEnumerable<GitHubGist> Sort(IEnumerable<GitHubGist> gists, GistLibrarySort sort) => sort switch
    {
        GistLibrarySort.Newest => gists.OrderByDescending(static gist => gist.CreatedAt).ThenBy(static gist => gist.Id, StringComparer.Ordinal),
        GistLibrarySort.Oldest => gists.OrderBy(static gist => gist.CreatedAt).ThenBy(static gist => gist.Id, StringComparer.Ordinal),
        GistLibrarySort.Title => gists.OrderBy(GetTitle, StringComparer.OrdinalIgnoreCase).ThenBy(static gist => gist.Id, StringComparer.Ordinal),
        _ => gists.OrderByDescending(static gist => gist.UpdatedAt).ThenBy(static gist => gist.Id, StringComparer.Ordinal)
    };

    public static bool HasSameListProjection(GitHubGist left, GitHubGist right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal)
        && string.Equals(left.Description, right.Description, StringComparison.Ordinal)
        && left.Public == right.Public
        && left.CreatedAt == right.CreatedAt
        && left.UpdatedAt == right.UpdatedAt
        && left.Files.Count == right.Files.Count
        && left.Files.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(right.Files.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
}

public sealed record GistLibraryProjectionSnapshot(
    GitHubGist[] Items,
    IReadOnlySet<string> Keys);

public readonly record struct GistProjectionApplyStatistics(
    int ItemCount,
    int OperationCount,
    int YieldCount,
    int MaximumOperationsInSlice);

public static class GistProjectionApplyPolicy
{
    public const int MaximumOperationsPerSlice = 64;

    public static readonly TimeSpan MaximumTimePerSlice = TimeSpan.FromMilliseconds(4);
}
