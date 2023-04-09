using Octokit;

namespace JitHub.Models.PRConversation
{
    public class EventNode : ConversationNode
    {
        public long Id { get; set; }
        public string NodeId { get; set; }
        public string Url { get; set; }
        public User Actor { get; set; }
        public User Assignee { get; set; }
        public EventInfoState State { get; set; }
        public Label Label { get; set; }
        public string CommitId { get; set; }
        public RenameInfo RenameInfo { get; set; }

        public EventNode(IssueEvent @event, Repository repo, int number) : base(repo, number)
        {
            CreatedAt = @event.CreatedAt;
            Id = @event.Id;
            NodeId = @event.NodeId;
            Url = @event.Url;
            Actor = @event.Actor;
            Assignee = @event.Assignee;
            State = @event.Event.Value;
            Label = @event.Label;
            CommitId = @event.CommitId;
            RenameInfo = @event.Rename;
        }
    }
}
