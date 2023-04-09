using Octokit;

namespace JitHub.Models.PRConversation
{
    public class CommitNode : ConversationNode
    {
        public string NodeId { get; set; }
        public User Author { get; set; }
        public User Committer { get; set; }
        public Commit Commit { get; set; }
        public string Sha { get; set; }

        public CommitNode(PullRequestCommit commit, Repository repo, int number) : base(repo, number)
        {
            NodeId = commit.NodeId;
            Author = commit.Author;
            Committer = commit.Committer;
            Commit = commit.Commit;
            Sha = commit.Sha;
            CreatedAt = Commit.Author.Date;
            Object = commit;
        }
    }
}
