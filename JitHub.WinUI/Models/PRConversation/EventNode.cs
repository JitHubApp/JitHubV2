using JitHub.Models.LegacyGitHub;

namespace JitHub.Models.PRConversation
{
    public class EventNode : ConversationNode
    {
        public long Id { get; set; }
        public string NodeId { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public User? Actor { get; set; }
        public User? Assignee { get; set; }
        public User? Assigner { get; set; }
        public EventInfoState State { get; set; }
        public Label? Label { get; set; }
        public string CommitId { get; set; } = string.Empty;
        public RenameInfo? RenameInfo { get; set; }
        public Team? RequestedTeam { get; set; }
        public User? ReviewRequester { get; set; }
        public User? RequestedReviewer { get; set; }
        public string LockReason { get; set; } = string.Empty;
        public Milestone? Milestone { get; set; }
        public object? DismissedReview { get; set; }

        public EventNode(IssueEvent @event, Repository repo, int number) : base(repo, number)
        {
            CreatedAt = @event.CreatedAt;
            Id = @event.Id;
            NodeId = @event.NodeId ?? string.Empty;
            Url = @event.Url ?? string.Empty;
            Actor = @event.Actor;
            Assignee = @event.Assignee;
            State = @event.Event.Value;
            Label = @event.Label;
            CommitId = @event.CommitId ?? string.Empty;
            RenameInfo = @event.Rename;
            RequestedTeam = @event.RequestedTeam;
            ReviewRequester = @event.ReviewRequester;
            RequestedReviewer = @event.RequestedReviewer;
            Assigner = @event.Assigner;
            LockReason = @event.LockReason ?? string.Empty;
            Milestone = @event.Milestone;
            DismissedReview = @event.DismissedReview;
        }
    }
}
