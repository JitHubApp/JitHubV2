using System;

namespace JitHub.Services;

public static class PullRequestReviewSubmissionPolicy
{
    public static void Validate(PullRequestReviewSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.Decision is PullRequestReviewDecision.Comment or PullRequestReviewDecision.RequestChanges &&
            string.IsNullOrWhiteSpace(submission.Body))
        {
            throw new ArgumentException(
                "A review comment is required when commenting or requesting changes.",
                nameof(submission));
        }
    }

    public static string ToApiEvent(PullRequestReviewDecision decision) => decision switch
    {
        PullRequestReviewDecision.Comment => "COMMENT",
        PullRequestReviewDecision.Approve => "APPROVE",
        PullRequestReviewDecision.RequestChanges => "REQUEST_CHANGES",
        _ => throw new ArgumentOutOfRangeException(nameof(decision))
    };
}
