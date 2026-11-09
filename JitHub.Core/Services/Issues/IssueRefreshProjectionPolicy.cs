using System;
using System.Collections.Generic;
using System.Globalization;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public static class IssueRefreshProjectionPolicy
{
    public static string CreateQueryIdentity(GitHubIssueQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.Join(
            "\n",
            Normalize(options.State),
            Normalize(options.Sort),
            Normalize(options.Direction),
            options.Since?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            Normalize(options.Labels),
            Normalize(options.Milestone),
            Normalize(options.Assignee),
            Normalize(options.Creator),
            Normalize(options.Mentioned),
            Normalize(options.Filter));
    }

    public static IReadOnlyList<GitHubIssue> PreserveExistingRowsOnPartialRefresh(
        IReadOnlyList<GitHubIssue> incoming,
        IReadOnlyList<GitHubIssue> existing,
        IssueSectionState state,
        bool isSameQuery)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(state);
        if (!isSameQuery || state.Completeness == PagedDataCompleteness.Complete || existing.Count == 0)
        {
            return incoming;
        }

        List<GitHubIssue> merged = new(incoming.Count + existing.Count);
        HashSet<long> seen = [];
        foreach (GitHubIssue issue in incoming)
        {
            if (seen.Add(CreateKey(issue)))
            {
                merged.Add(issue);
            }
        }

        foreach (GitHubIssue issue in existing)
        {
            if (seen.Add(CreateKey(issue)))
            {
                merged.Add(issue);
            }
        }

        return merged;
    }

    public static bool ShouldPreserveVisibleSection<T>(
        IssuePagedSection<T> section,
        int visibleItemCount)
        where T : class =>
        visibleItemCount > 0 &&
        section.Items.Length == 0 &&
        (section.State.CacheState == CacheState.Error ||
         !string.IsNullOrWhiteSpace(section.State.ErrorMessage));

    public static IReadOnlyList<T> PreserveExistingSectionOnPartialRefresh<T, TKey>(
        IssuePagedSection<T> section,
        IReadOnlyList<T> existing,
        Func<T, TKey> keySelector)
        where T : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(keySelector);
        if (existing.Count == 0 || section.State.Completeness == PagedDataCompleteness.Complete)
        {
            return section.Items;
        }

        List<T> merged = new(section.Items.Length + existing.Count);
        HashSet<TKey> seen = [];
        foreach (T item in section.Items)
        {
            if (seen.Add(keySelector(item)))
            {
                merged.Add(item);
            }
        }

        foreach (T item in existing)
        {
            if (seen.Add(keySelector(item)))
            {
                merged.Add(item);
            }
        }

        return merged;
    }

    private static long CreateKey(GitHubIssue issue) => issue.Id != 0 ? issue.Id : issue.Number;

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
