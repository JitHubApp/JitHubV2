using System.Collections;
using JitHub.Models.GitHub;
using MarkdownRenderer.Images;

namespace JitHub.WinUI.ViewModels.Pages;

public interface IPullRequestReviewItem
{
    string AutomationId { get; }

    string ReviewerLogin { get; }

    string? ReviewerProfileLogin { get; }

    string ReviewerAvatarUrl { get; }

    string StateText { get; }

    string SubmittedAtText { get; }

    MarkdownDocumentSource? MarkdownSource { get; }

    string BodyText { get; }

    IEnumerable Threads { get; }
}

public interface IPullRequestReviewThreadItem
{
    long CommentId { get; }

    string CommentNodeId { get; }

    string AutomationId { get; }

    string ReplyAutomationId { get; }

    string ReplyFormAutomationId { get; }

    MarkdownDocumentSource? MarkdownSource { get; }

    MarkdownDocumentSource? ReplyMarkdownSource { get; }

    string PathDisplayText { get; }

    string CreatedAtText { get; }

    string DiffHunkText { get; }

    string CommentBody { get; }

    string CommentAuthorLogin { get; }

    string CommentHtmlUrl { get; }

    GitHubReactionSummary Reactions { get; }

    bool IsMinimized { get; }

    string ViewerLogin { get; }

    bool CanReact { get; }

    bool CanReply { get; }

    bool CanModerate { get; }

    string ReplyButtonText { get; }

    string ReplyText { get; set; }

    bool IsReplyEnabled { get; }

    IEnumerable Replies { get; }
}
