using System;
using System.Collections.Generic;
using System.Linq;

namespace JitHub.Services;

public static class PullRequestSectionProjectionPolicy
{
    public static bool IsTerminalListResult(PullRequestSectionState state) =>
        !state.IsRefreshInProgress &&
        string.IsNullOrWhiteSpace(state.ErrorMessage) &&
        state.CacheState != CacheState.Error &&
        state.Completeness is PagedDataCompleteness.Complete or PagedDataCompleteness.ApiLimited;

    public static string CreateListTelemetryResult(PagedDataCompleteness completeness) => completeness switch
    {
        PagedDataCompleteness.Complete => "success",
        PagedDataCompleteness.ApiLimited => "api_limited",
        _ => "partial"
    };

    public static string CreateListScopeNotice(PullRequestSectionState state) => state.Completeness switch
    {
        PagedDataCompleteness.Partial =>
            $"{state.LoadedItemCount:N0} pull requests loaded. Some results could not be refreshed.",
        PagedDataCompleteness.ApiLimited when state.ApiLimit is > 0 =>
            $"{state.LoadedItemCount:N0} pull requests indexed (GitHub API limit: {state.ApiLimit.Value:N0}).",
        PagedDataCompleteness.ApiLimited =>
            $"{state.LoadedItemCount:N0} pull requests indexed (GitHub API limit).",
        _ => string.Empty
    };

    public static T[] ProjectSection<T>(
        IEnumerable<T> incoming,
        IEnumerable<T> published,
        PullRequestSectionState state,
        Func<T, string> keySelector) =>
        ProjectSection(incoming, published, keySelector, state);

    public static T[] ProjectSection<T>(
        IEnumerable<T> incoming,
        IEnumerable<T> published,
        Func<T, string> keySelector,
        params PullRequestSectionState[] states)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(states);

        bool isAuthoritative = states.Length > 0 && states.All(static state =>
            string.IsNullOrWhiteSpace(state.ErrorMessage)
                && state.Completeness == PagedDataCompleteness.Complete);
        return PagedRefreshProjectionPolicy.Merge(
            incoming,
            published,
            keySelector,
            isAuthoritative ? PagedDataCompleteness.Complete : PagedDataCompleteness.Partial);
    }

    public static T[] ProjectSection<T>(
        IEnumerable<T> incoming,
        IEnumerable<T> published,
        PullRequestSectionState state,
        Func<T, int, string> keySelector)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(keySelector);

        bool isAuthoritative = string.IsNullOrWhiteSpace(state.ErrorMessage)
            && state.Completeness == PagedDataCompleteness.Complete;
        return PagedRefreshProjectionPolicy.Merge(
            incoming,
            published,
            keySelector,
            isAuthoritative ? PagedDataCompleteness.Complete : PagedDataCompleteness.Partial);
    }

    public static CommitDiffDocument ProjectDiffDocument(
        CommitDiffDocument incoming,
        CommitDiffDocument published,
        PullRequestSectionState state)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(published);
        CommitDiffFile[] files = ProjectSection(
            incoming.Files,
            published.Files,
            state,
            static file => file.Filename);
        return files.Length == 0 ? CommitDiffDocument.Empty : new CommitDiffDocument(files);
    }

    public static string CreateErrorText(PullRequestOverviewAggregate aggregate)
    {
        List<string> failedSections = [];
        List<string> scopeNotices = [];
        AddFailedSection(failedSections, aggregate.PullRequestState, "pull request");
        AddFailedSection(failedSections, aggregate.IssueState, "metadata");
        AddScopeNotice(scopeNotices, aggregate.PullRequestState, "Pull request");
        AddScopeNotice(scopeNotices, aggregate.IssueState, "Metadata");
        return CombineNotices(CreateErrorText(failedSections), scopeNotices);
    }

    public static string CreateErrorText(PullRequestDetailAggregate aggregate)
    {
        List<string> failedSections = [];
        List<string> scopeNotices = [];
        AddFailedSection(failedSections, aggregate.PullRequestState, "pull request");
        AddFailedSection(failedSections, aggregate.IssueState, "metadata");
        AddFailedSection(failedSections, aggregate.CommentsState, "comments");
        AddFailedSection(failedSections, aggregate.CommitsState, "commits");
        AddFailedSection(failedSections, aggregate.ReviewsState, "reviews");
        AddFailedSection(failedSections, aggregate.ReviewCommentsState, "review comments");
        AddFailedSection(failedSections, aggregate.TimelineState, "timeline");
        AddScopeNotice(scopeNotices, aggregate.PullRequestState, "Pull request");
        AddScopeNotice(scopeNotices, aggregate.IssueState, "Metadata");
        AddScopeNotice(scopeNotices, aggregate.CommentsState, "Comments");
        AddScopeNotice(scopeNotices, aggregate.CommitsState, "Commits");
        AddScopeNotice(scopeNotices, aggregate.ReviewsState, "Reviews");
        AddScopeNotice(scopeNotices, aggregate.ReviewCommentsState, "Review comments");
        AddScopeNotice(scopeNotices, aggregate.TimelineState, "Timeline");
        return CombineNotices(CreateErrorText(failedSections), scopeNotices);
    }

    public static string CreateErrorText(PullRequestConversationAggregate aggregate)
    {
        List<string> failedSections = [];
        List<string> scopeNotices = [];
        AddFailedSection(failedSections, aggregate.PullRequestState, "pull request");
        AddFailedSection(failedSections, aggregate.IssueState, "metadata");
        AddFailedSection(failedSections, aggregate.CommentsState, "comments");
        AddScopeNotice(scopeNotices, aggregate.PullRequestState, "Pull request");
        AddScopeNotice(scopeNotices, aggregate.IssueState, "Metadata");
        AddScopeNotice(scopeNotices, aggregate.CommentsState, "Comments");
        return CombineNotices(CreateErrorText(failedSections), scopeNotices);
    }

    public static string CreateSectionErrorText(
        PullRequestWorkspaceSection section,
        params PullRequestSectionState[] states)
    {
        string sectionName = section switch
        {
            PullRequestWorkspaceSection.Commits => "commits",
            PullRequestWorkspaceSection.Reviews => "reviews",
            PullRequestWorkspaceSection.Timeline => "timeline",
            _ => "conversation"
        };
        if (states.Any(static state => !string.IsNullOrWhiteSpace(state.ErrorMessage)))
        {
            return $"Could not refresh {sectionName}. Existing content remains available.";
        }

        PullRequestSectionState? limited = states.FirstOrDefault(
            static state => state.Completeness == PagedDataCompleteness.ApiLimited);
        if (limited is not null)
        {
            return CreateApiLimitNotice(sectionName, limited.ApiLimit);
        }

        return states.Any(static state => state.Completeness == PagedDataCompleteness.Partial)
            ? $"{Capitalize(sectionName)} are partially loaded. Available content remains visible."
            : string.Empty;
    }

    private static string CreateErrorText(IReadOnlyCollection<string> failedSections) =>
        failedSections.Count == 0
            ? string.Empty
            : $"Could not refresh {string.Join(", ", failedSections)}. Existing content remains available.";

    private static void AddFailedSection(
        ICollection<string> failedSections,
        PullRequestSectionState state,
        string sectionName)
    {
        if (!string.IsNullOrWhiteSpace(state.ErrorMessage))
        {
            failedSections.Add(sectionName);
        }
    }

    private static void AddScopeNotice(
        ICollection<string> notices,
        PullRequestSectionState state,
        string sectionName)
    {
        if (!string.IsNullOrWhiteSpace(state.ErrorMessage))
        {
            return;
        }

        if (state.Completeness == PagedDataCompleteness.ApiLimited)
        {
            notices.Add(CreateApiLimitNotice(sectionName.ToLowerInvariant(), state.ApiLimit));
        }
        else if (state.Completeness == PagedDataCompleteness.Partial)
        {
            notices.Add($"{sectionName} are partially loaded. Available content remains visible.");
        }
    }

    private static string CreateApiLimitNotice(string sectionName, int? apiLimit) =>
        apiLimit is > 0
            ? $"Only the first {apiLimit.Value:N0} {sectionName} are available because of GitHub's API limit."
            : $"{Capitalize(sectionName)} reached GitHub's API limit.";

    private static string CombineNotices(string errorText, IReadOnlyCollection<string> scopeNotices)
    {
        if (scopeNotices.Count == 0)
        {
            return errorText;
        }

        string scopeText = string.Join(" ", scopeNotices);
        return string.IsNullOrWhiteSpace(errorText) ? scopeText : $"{errorText} {scopeText}";
    }

    private static string Capitalize(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
