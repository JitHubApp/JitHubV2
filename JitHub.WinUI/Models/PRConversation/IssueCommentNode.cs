using JitHub.Services;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.LegacyGitHub;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;

namespace JitHub.Models.PRConversation
{
    [WinRT.GeneratedBindableCustomProperty]
    public partial class IssueCommentNode : ConversationNode
    {
        public long Id { get; set; }
        public string NodeId { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public User User { get; set; } = null!;
        public ReactionSummary Reaction { get; set; } = new();
        public ICommand CopyLinkCommand { get; set; } = null!;
        public ICommand? QuoteReplyCommand { get; set; }

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
        }

        private void CopyLink()
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(Url);
            Clipboard.SetContent(dataPackage);
        }
    }
}
