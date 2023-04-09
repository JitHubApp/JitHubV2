using JitHub.Models.PRConversation;
using Octokit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.Helpers
{
    public class IssueEventTemplateSelector : DataTemplateSelector
    {
        public DataTemplate ClosedTemplate { get; set; }
        public DataTemplate ReopenedTemplate { get; set; }
        public DataTemplate SubscribedTemplate { get; set; }
        public DataTemplate MergedTemplate { get; set; }
        public DataTemplate ReferencedTemplate { get; set; }
        public DataTemplate MentionedTemplate { get; set; }
        public DataTemplate AssignedTemplate { get; set; }
        public DataTemplate UnassignedTemplate { get; set; }
        public DataTemplate LabeledTemplate { get; set; }
        public DataTemplate UnlabeledTemplate { get; set; }
        public DataTemplate MilestonedTemplate { get; set; }
        public DataTemplate DemilestonedTemplate { get; set; }
        public DataTemplate RenamedTemplate { get; set; }
        public DataTemplate LockedTemplate { get; set; }
        public DataTemplate UnlockedTemplate { get; set; }
        public DataTemplate HeadRefDeletedTemplate { get; set; }
        public DataTemplate HeadRefRestoredTemplate { get; set; }
        public DataTemplate HeadRefForcePushedTemplate { get; set; }
        public DataTemplate ReadyForReviewTemplate { get; set; }
        public DataTemplate ReviewDismissedTemplate { get; set; }
        public DataTemplate ReviewRequestedTemplate { get; set; }
        public DataTemplate ReviewRequestRemovedTemplate { get; set; }
        public DataTemplate AddedToProjectTemplate { get; set; }
        public DataTemplate MovedColumnsInProjectTemplate { get; set; }
        public DataTemplate RemovedFromProjectTemplate { get; set; }
        public DataTemplate ConvertedNoteToIssueTemplate { get; set; }
        public DataTemplate UnsubscribedTemplate { get; set; }
        public DataTemplate CommentedTemplate { get; set; }
        public DataTemplate CommittedTemplate { get; set; }
        public DataTemplate BaseRefChangedTemplate { get; set; }
        public DataTemplate CrossreferencedTemplate { get; set; }
        public DataTemplate ReviewedTemplate { get; set; }
        public DataTemplate LineCommentedTemplate { get; set; }
        public DataTemplate CommitCommentedTemplate { get; set; }
        public DataTemplate MarkedAsDuplicateTemplate { get; set; }
        public DataTemplate UnmarkedAsDuplicateTemplate { get; set; }
        public DataTemplate CommentDeletedTemplate { get; set; }
        public DataTemplate TransferredTemplate { get; set; }
        public DataTemplate ConnectedTemplate { get; set; }
        public DataTemplate PinnedTemplate { get; set; }
        public DataTemplate UnpinnedTemplate { get; set; }
        public DataTemplate DefaultTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            var node = item as EventNode;

            switch (node.State)
            {
                case EventInfoState.Closed:
                    return ClosedTemplate;
                case EventInfoState.Reopened:
                    return ReopenedTemplate;
                case EventInfoState.Subscribed:
                    return SubscribedTemplate;
                case EventInfoState.Merged:
                    return MergedTemplate;
                case EventInfoState.Referenced:
                    return ReferencedTemplate;
                case EventInfoState.Mentioned:
                    return MentionedTemplate;
                case EventInfoState.Assigned:
                    return AssignedTemplate;
                case EventInfoState.Unassigned:
                    return UnassignedTemplate;
                case EventInfoState.Labeled:
                    return LabeledTemplate;
                case EventInfoState.Unlabeled:
                    return UnlabeledTemplate;
                case EventInfoState.Milestoned:
                    return MilestonedTemplate;
                case EventInfoState.Demilestoned:
                    return DemilestonedTemplate;
                case EventInfoState.Renamed:
                    return RenamedTemplate;
                case EventInfoState.Locked:
                    return LockedTemplate;
                case EventInfoState.Unlocked:
                    return UnlockedTemplate;
                case EventInfoState.HeadRefDeleted:
                    return HeadRefDeletedTemplate;
                case EventInfoState.HeadRefRestored:
                    return HeadRefRestoredTemplate;
                case EventInfoState.HeadRefForcePushed:
                    return HeadRefForcePushedTemplate;
                case EventInfoState.ReadyForReview:
                    return ReadyForReviewTemplate;
                case EventInfoState.ReviewDismissed:
                    return ReviewDismissedTemplate;
                case EventInfoState.ReviewRequested:
                    return ReviewRequestedTemplate;
                case EventInfoState.ReviewRequestRemoved:
                    return ReviewRequestRemovedTemplate;
                case EventInfoState.AddedToProject:
                    return AddedToProjectTemplate;
                case EventInfoState.MovedColumnsInProject:
                    return MovedColumnsInProjectTemplate;
                case EventInfoState.RemovedFromProject:
                    return RemovedFromProjectTemplate;
                case EventInfoState.ConvertedNoteToIssue:
                    return ConvertedNoteToIssueTemplate;
                case EventInfoState.Unsubscribed:
                    return UnsubscribedTemplate;
                case EventInfoState.Commented:
                    return CommentedTemplate;
                case EventInfoState.Committed:
                    return CommittedTemplate;
                case EventInfoState.BaseRefChanged:
                    return BaseRefChangedTemplate;
                case EventInfoState.Crossreferenced:
                    return CrossreferencedTemplate;
                case EventInfoState.Reviewed:
                    return ReviewedTemplate;
                case EventInfoState.LineCommented:
                    return LineCommentedTemplate;
                case EventInfoState.CommitCommented:
                    return CommitCommentedTemplate;
                case EventInfoState.MarkedAsDuplicate:
                    return MarkedAsDuplicateTemplate;
                case EventInfoState.UnmarkedAsDuplicate:
                    return UnmarkedAsDuplicateTemplate;
                case EventInfoState.CommentDeleted:
                    return CommentDeletedTemplate;
                case EventInfoState.Transferred:
                    return TransferredTemplate;
                case EventInfoState.Connected:
                    return ConnectedTemplate;
                case EventInfoState.Pinned:
                    return PinnedTemplate;
                case EventInfoState.Unpinned:
                    return UnpinnedTemplate;
                default:
                    return DefaultTemplate;

            }
        }
    }
}
