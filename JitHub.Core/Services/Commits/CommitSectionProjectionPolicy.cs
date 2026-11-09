using System;
using System.Collections.Generic;

namespace JitHub.Services;

public static class CommitSectionProjectionPolicy
{
    public static T[] Project<T>(
        IEnumerable<T> incoming,
        IEnumerable<T> published,
        CommitSectionState state,
        Func<T, string> keySelector)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(keySelector);

        bool isAuthoritative = string.IsNullOrWhiteSpace(state.ErrorMessage)
            && state.Completeness == PagedDataCompleteness.Complete;
        return PagedRefreshProjectionPolicy.Merge(
            incoming,
            published,
            keySelector,
            isAuthoritative ? PagedDataCompleteness.Complete : PagedDataCompleteness.Partial);
    }
}
