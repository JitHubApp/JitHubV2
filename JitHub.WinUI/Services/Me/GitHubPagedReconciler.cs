using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public sealed record GitHubPagedLoadProgress<TItem>(
    IReadOnlyList<TItem> Items,
    int? TotalCount,
    int LoadedPageCount,
    bool IsFinal,
    PagedDataCompleteness Completeness = PagedDataCompleteness.Loading,
    bool IsAuthoritative = false);

public sealed record GitHubPagedLoadResult<TItem>(
    IReadOnlyList<TItem> Items,
    int? TotalCount,
    int LoadedPageCount,
    PagedDataCompleteness Completeness = PagedDataCompleteness.Complete);

public enum PagedDataCompleteness
{
    Loading,
    Complete,
    Partial,
    ApiLimited
}

public static class GitHubPagedReconciler
{
    public static async Task<GitHubPagedLoadResult<TItem>> LoadAsync<TPage, TItem>(
        Func<int, CancellationToken, Task<CachedResult<TPage>>> getPageAsync,
        Func<int, CancellationToken, Task<CachedResult<TPage>>> refreshPageAsync,
        Func<TPage, IReadOnlyList<TItem>> itemSelector,
        Func<TPage, int?>? totalCountSelector,
        Func<TItem, string> keySelector,
        int pageSize,
        int maximumItemCount,
        Action<GitHubPagedLoadProgress<TItem>>? progress = null,
        CancellationToken cancellationToken = default)
        where TPage : class
    {
        ArgumentNullException.ThrowIfNull(getPageAsync);
        ArgumentNullException.ThrowIfNull(refreshPageAsync);
        ArgumentNullException.ThrowIfNull(itemSelector);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItemCount);

        int maximumPageCount = (int)Math.Ceiling(maximumItemCount / (double)pageSize);
        SortedDictionary<int, IReadOnlyList<TItem>> pages = [];
        IReadOnlyList<TItem> flattened = [];
        int? reportedTotalCount = null;
        int loadedPageCount = 0;
        PagedDataCompleteness completeness = PagedDataCompleteness.Partial;

        for (int pageNumber = 1; pageNumber <= maximumPageCount; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int previousUniqueCount = flattened.Count;

            CachedResult<TPage> cachedPage = await getPageAsync(pageNumber, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<TItem> cachedItems = SelectPageItems(cachedPage.Value, itemSelector, pageSize);
            pages[pageNumber] = cachedItems;
            loadedPageCount = pageNumber;
            reportedTotalCount = ReadTotalCount(cachedPage.Value, totalCountSelector) ?? reportedTotalCount;
            flattened = FlattenPages(pages, keySelector, maximumItemCount);
            progress?.Invoke(new GitHubPagedLoadProgress<TItem>(
                flattened,
                reportedTotalCount,
                loadedPageCount,
                IsFinal: false,
                IsAuthoritative: false));

            CachedResult<TPage> authoritativePage = cachedPage;
            IReadOnlyList<TItem> authoritativeItems = cachedItems;
            if (RequiresAuthoritativeRefresh(cachedPage))
            {
                authoritativePage = await refreshPageAsync(pageNumber, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                authoritativeItems = SelectPageItems(authoritativePage.Value, itemSelector, pageSize);
                pages[pageNumber] = authoritativeItems;
                reportedTotalCount = ReadTotalCount(authoritativePage.Value, totalCountSelector) ?? reportedTotalCount;
                flattened = FlattenPages(pages, keySelector, maximumItemCount);
                progress?.Invoke(new GitHubPagedLoadProgress<TItem>(
                    flattened,
                    reportedTotalCount,
                    loadedPageCount,
                    IsFinal: false,
                    IsAuthoritative: false));
            }

            int? authoritativeTotalCount = ReadTotalCount(authoritativePage.Value, totalCountSelector);
            int boundedTotalCount = authoritativeTotalCount.HasValue
                ? Math.Min(Math.Max(0, authoritativeTotalCount.Value), maximumItemCount)
                : 0;
            bool totalIndicatesAnotherPage = authoritativeTotalCount.HasValue && flattened.Count < boundedTotalCount;
            bool fullPageIndicatesAnotherPage = authoritativeItems.Count >= pageSize;
            bool madeProgress = flattened.Count > previousUniqueCount;

            if (flattened.Count >= maximumItemCount)
            {
                completeness = PagedDataCompleteness.ApiLimited;
                break;
            }

            if (authoritativeItems.Count == 0 ||
                (!totalIndicatesAnotherPage && !fullPageIndicatesAnotherPage))
            {
                completeness = PagedDataCompleteness.Complete;
                break;
            }

            if (!madeProgress)
            {
                completeness = PagedDataCompleteness.Partial;
                break;
            }
        }

        progress?.Invoke(new GitHubPagedLoadProgress<TItem>(
            flattened,
            reportedTotalCount,
            loadedPageCount,
            IsFinal: true,
            completeness,
            IsAuthoritative: completeness == PagedDataCompleteness.Complete));
        return new GitHubPagedLoadResult<TItem>(flattened, reportedTotalCount, loadedPageCount, completeness);
    }

    internal static bool RequiresAuthoritativeRefresh<T>(CachedResult<T> result)
        where T : class =>
        result.IsRefreshInProgress || result.CacheState is not CacheState.Fresh;

    private static IReadOnlyList<TItem> SelectPageItems<TPage, TItem>(
        TPage? page,
        Func<TPage, IReadOnlyList<TItem>> itemSelector,
        int pageSize)
        where TPage : class
    {
        if (page is null)
        {
            return [];
        }

        IReadOnlyList<TItem> items = itemSelector(page) ?? [];
        return items.Count <= pageSize ? items : items.Take(pageSize).ToArray();
    }

    private static int? ReadTotalCount<TPage>(TPage? page, Func<TPage, int?>? totalCountSelector)
        where TPage : class =>
        page is null || totalCountSelector is null ? null : totalCountSelector(page);

    private static IReadOnlyList<TItem> FlattenPages<TItem>(
        IEnumerable<KeyValuePair<int, IReadOnlyList<TItem>>> pages,
        Func<TItem, string> keySelector,
        int maximumItemCount)
    {
        List<TItem> flattened = [];
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (KeyValuePair<int, IReadOnlyList<TItem>> page in pages)
        {
            foreach (TItem item in page.Value)
            {
                if (keys.Add(keySelector(item)))
                {
                    flattened.Add(item);
                    if (flattened.Count >= maximumItemCount)
                    {
                        return flattened;
                    }
                }
            }
        }

        return flattened;
    }
}
