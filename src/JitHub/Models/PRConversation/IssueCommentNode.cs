using JitHub.Services;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;

namespace JitHub.Models.PRConversation
{
    public class IssueCommentNode : ConversationNode
    {
        public int Id { get; set; }
        public string NodeId { get; set; }
        public string Url { get; set; }
        public string Body { get; set; }
        public User User { get; set; }
        public ReactionSummary Reaction { get; set; }
        public ICommand CopyLinkCommand { get; set; }
        public ICommand QuoteReplyCommand { get; set; }

        public IssueCommentNode(IssueComment reviewComment, Repository repo, int number) : base(repo, number)
        {
            CreatedAt = reviewComment.CreatedAt;
            Object = reviewComment;
            Id = reviewComment.Id;
            NodeId = reviewComment.NodeId;
            Url = reviewComment.HtmlUrl;
            Reaction = reviewComment.Reactions;
            Body = reviewComment.Body;
            User = reviewComment.User;
            CopyLinkCommand = new RelayCommand(CopyLink);
            var gitHubService = Ioc.Default.GetService<IGitHubService>();
        }

        private void CopyLink()
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(Url);
            Clipboard.SetContent(dataPackage);
        }
    }
}
