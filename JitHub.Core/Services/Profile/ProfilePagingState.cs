using System;
using System.Collections.Generic;
using System.Linq;

namespace JitHub.Services;

public sealed class ProfilePagingState
{
    public int NextPage { get; private set; } = 1;

    public bool IsLoading { get; private set; }

    public bool IsComplete { get; private set; }

    public bool TryBegin(out int page)
    {
        page = NextPage;
        if (IsLoading || IsComplete)
        {
            return false;
        }

        IsLoading = true;
        return true;
    }

    public void Complete(int page, int returnedCount, int pageSize, bool isTerminal = false)
    {
        if (!IsLoading || page != NextPage)
        {
            throw new InvalidOperationException("The completed profile page does not match the active request.");
        }

        IsLoading = false;
        IsComplete = isTerminal || returnedCount < pageSize;
        if (!IsComplete)
        {
            NextPage++;
        }
    }

    public void Fail()
    {
        IsLoading = false;
    }

    public void Reset()
    {
        NextPage = 1;
        IsLoading = false;
        IsComplete = false;
    }
}

public sealed record ProfileSectionPageDecision(
    bool ApplyPage,
    bool CompletePage,
    bool MarkModeLoaded);

public static class ProfileSectionLoadPolicy
{
    public static IReadOnlyList<T> MergePartialSnapshot<T>(
        IEnumerable<T> published,
        IEnumerable<T> refreshedPrefix,
        Func<T, string> keySelector)
    {
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(refreshedPrefix);
        ArgumentNullException.ThrowIfNull(keySelector);

        T[] prefix = refreshedPrefix.ToArray();
        HashSet<string> refreshedKeys = prefix
            .Select(keySelector)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return prefix
            .Concat(published.Where(item => !refreshedKeys.Contains(keySelector(item))))
            .ToArray();
    }

    public static ProfileSectionPageDecision Decide(
        int page,
        int publishedCount,
        int returnedCount,
        bool hasError)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page);
        ArgumentOutOfRangeException.ThrowIfNegative(publishedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(returnedCount);

        if (!hasError)
        {
            return new ProfileSectionPageDecision(
                ApplyPage: true,
                CompletePage: true,
                MarkModeLoaded: true);
        }

        bool canAddFailedPageRows = returnedCount > 0 && (publishedCount == 0 || page > 1);
        return new ProfileSectionPageDecision(
            ApplyPage: canAddFailedPageRows,
            CompletePage: false,
            MarkModeLoaded: false);
    }

    public static string FormatStatus(
        string section,
        int visibleCount,
        int resultItemCount,
        CacheState cacheState,
        bool hasError,
        PagedDataCompleteness completeness,
        bool visibleRowsAreCached = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentOutOfRangeException.ThrowIfNegative(visibleCount);
        ArgumentOutOfRangeException.ThrowIfNegative(resultItemCount);

        if (hasError)
        {
            if (visibleCount == 0)
            {
                return $"Could not load {section}.";
            }

            bool allVisibleRowsAreCached = visibleRowsAreCached
                && cacheState is CacheState.Stale or CacheState.Refreshing;
            return allVisibleRowsAreCached
                ? $"Showing {visibleCount} cached {section}; refresh is incomplete."
                : $"Showing {visibleCount} loaded {section}; refresh is incomplete.";
        }

        return completeness switch
        {
            PagedDataCompleteness.Partial => $"Showing {visibleCount} loaded {section} (partial).",
            PagedDataCompleteness.ApiLimited => $"Showing {visibleCount} {section} (GitHub API limit).",
            _ => string.Empty
        };
    }
}
