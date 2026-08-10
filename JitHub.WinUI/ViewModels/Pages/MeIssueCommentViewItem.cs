using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.Helpers;
using MarkdownRenderer.Images;

namespace JitHub.WinUI.ViewModels.Pages;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class MeIssueCommentViewItem : ObservableObject
{
    public MeIssueCommentViewItem()
    {
    }

    public MeIssueCommentViewItem(GitHubIssueComment comment)
    {
        Comment = comment ?? throw new ArgumentNullException(nameof(comment));
    }

    public GitHubIssueComment Comment { get; private set; } = new();

    public string StableKey => GetStableKey(Comment);

    public string AuthorDisplayName => string.IsNullOrWhiteSpace(Comment.User?.Login)
        ? LocalizedResourceText.GetString("Common.UnknownUser", "unknown")
        : Comment.User.Login;

    public string? AuthenticatedLogin =>
        UserIdentityNavigationPolicy.GetRoutableLogin(Comment.User?.Login);

    public string AuthorAvatarUrl => Comment.User?.AvatarUrl ?? string.Empty;

    public string Body => Comment.Body;

    public string CreatedText => FormatTimeAgo(Comment.CreatedAt);

    public string MarkdownAutomationId => Comment.MarkdownAutomationId;

    public string AutomationId => $"MyWorkItemComment_{StableKey}";

    public string AutomationName => $"Comment by {AuthorDisplayName}, {CreatedText}";

    public MarkdownDocumentSource? MarkdownSource => Comment.MarkdownSource;

    public bool ApplyComment(GitHubIssueComment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        if (HasSameProjection(Comment, comment))
        {
            return false;
        }

        Comment = comment;
        OnPropertyChanged(nameof(Comment));
        OnPropertyChanged(nameof(StableKey));
        OnPropertyChanged(nameof(AuthorDisplayName));
        OnPropertyChanged(nameof(AuthenticatedLogin));
        OnPropertyChanged(nameof(AuthorAvatarUrl));
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(CreatedText));
        OnPropertyChanged(nameof(MarkdownAutomationId));
        OnPropertyChanged(nameof(AutomationId));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(MarkdownSource));
        return true;
    }

    public static string GetStableKey(GitHubIssueComment comment) => comment.Id > 0
        ? comment.Id.ToString(CultureInfo.InvariantCulture)
        : $"{comment.HtmlUrl}|{comment.CreatedAt:O}";

    private static string FormatTimeAgo(DateTimeOffset value)
    {
        TimeSpan age = DateTimeOffset.Now - value.ToLocalTime();
        if (age.TotalMinutes < 1)
        {
            return LocalizedResourceText.GetString("Common.Time.JustNow", "just now");
        }

        if (age.TotalHours < 1)
        {
            return LocalizedResourceText.Format(
                "Common.Time.MinutesAgoFormat",
                "{0}m ago",
                (int)Math.Max(1, age.TotalMinutes));
        }

        if (age.TotalDays < 1)
        {
            return LocalizedResourceText.Format(
                "Common.Time.HoursAgoFormat",
                "{0}h ago",
                (int)Math.Max(1, age.TotalHours));
        }

        return age.TotalDays < 30
            ? LocalizedResourceText.Format(
                "Common.Time.DaysAgoFormat",
                "{0}d ago",
                (int)Math.Max(1, age.TotalDays))
            : value.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }

    private static bool HasSameProjection(GitHubIssueComment left, GitHubIssueComment right) =>
        left.Id == right.Id
        && left.CreatedAt == right.CreatedAt
        && left.UpdatedAt == right.UpdatedAt
        && string.Equals(left.NodeId, right.NodeId, StringComparison.Ordinal)
        && string.Equals(left.HtmlUrl, right.HtmlUrl, StringComparison.Ordinal)
        && string.Equals(left.Url, right.Url, StringComparison.Ordinal)
        && string.Equals(left.Body, right.Body, StringComparison.Ordinal)
        && string.Equals(left.AuthorAssociation, right.AuthorAssociation, StringComparison.Ordinal)
        && string.Equals(left.User?.Login, right.User?.Login, StringComparison.Ordinal)
        && string.Equals(left.User?.AvatarUrl, right.User?.AvatarUrl, StringComparison.Ordinal);
}
