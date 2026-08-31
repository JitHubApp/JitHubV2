using System;
using System.Collections.Generic;
using System.Linq;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public enum RepositoryLibraryFilter
{
    All,
    Public,
    Private,
    Forked,
    Archived
}

public enum RepositoryLibrarySort
{
    RecentlyUpdated,
    Name,
    MostStars
}

public static class RepositoryLibraryProjection
{
    public static IReadOnlyList<GitHubRepository> Apply(
        IEnumerable<GitHubRepository> repositories,
        string? searchText,
        RepositoryLibraryFilter filter,
        RepositoryLibrarySort sort)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        string search = searchText?.Trim() ?? string.Empty;
        IEnumerable<GitHubRepository> query = repositories;

        query = filter switch
        {
            RepositoryLibraryFilter.Public => query.Where(static repository => !repository.Private && !repository.Fork && !repository.Archived),
            RepositoryLibraryFilter.Private => query.Where(static repository => repository.Private && !repository.Fork && !repository.Archived),
            RepositoryLibraryFilter.Forked => query.Where(static repository => repository.Fork && !repository.Archived),
            RepositoryLibraryFilter.Archived => query.Where(static repository => repository.Archived),
            _ => query
        };

        if (search.Length > 0)
        {
            query = query.Where(repository => MatchesSearch(repository, search));
        }

        query = sort switch
        {
            RepositoryLibrarySort.Name => query
                .OrderBy(static repository => repository.FullName, StringComparer.CurrentCultureIgnoreCase),
            RepositoryLibrarySort.MostStars => query
                .OrderByDescending(static repository => repository.StargazersCount)
                .ThenBy(static repository => repository.FullName, StringComparer.CurrentCultureIgnoreCase),
            _ => query
                .OrderByDescending(static repository => repository.UpdatedAt ?? repository.PushedAt ?? DateTimeOffset.MinValue)
                .ThenBy(static repository => repository.FullName, StringComparer.CurrentCultureIgnoreCase)
        };

        return query
            .GroupBy(RepositoryKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
    }

    public static string RepositoryKey(GitHubRepository repository) =>
        repository.Id > 0
            ? repository.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : repository.FullName.Trim().ToLowerInvariant();

    private static bool MatchesSearch(GitHubRepository repository, string search)
    {
        StringComparison comparison = StringComparison.CurrentCultureIgnoreCase;
        return repository.FullName.Contains(search, comparison) ||
               repository.Name.Contains(search, comparison) ||
               repository.Owner.Login.Contains(search, comparison) ||
               (repository.Description?.Contains(search, comparison) ?? false) ||
               (repository.Language?.Contains(search, comparison) ?? false) ||
               repository.Topics.Any(topic => topic.Contains(search, comparison));
    }
}
