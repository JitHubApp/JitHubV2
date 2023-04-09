using Octokit;
using System;

namespace JitHub.Models.PRConversation
{
    public class ConversationNode
    {
        public Repository Repo { get; set; }
        // issue number or pr number
        // necessary field
        public int Number { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public object Object { get; set; }
        public ConversationNode Item => this;

        public ConversationNode(Repository repo, int number)
        {
            Repo = repo;
            Number = number;
        }
    }
}
