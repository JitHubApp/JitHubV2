using JitHub.WinUI.Helpers;
using JitHub.Models;
using JitHub.Models.PRConversation;
using JitHub.WinUI.ViewModels.Base;
using JitHub.WinUI.ViewModels.IssueViewModels;
using JitHub.WinUI.ViewModels.UserViewModel;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows.Input;
using Microsoft.UI.Xaml;

namespace JitHub.WinUI.ViewModels.PullRequestViewModels.ConversationViewModels
{
    public class ReviewCommentViewModel : RepoViewModel
    {
        private readonly ICommand _quoteReplyCommand;
        private bool _replyBoxExpanded;
        private string _replyText = string.Empty;
        private string _diffHunk = string.Empty;
        private string _name = string.Empty;
        private User _author = null!;
        private ReviewCommentNode _reviewCommentNode = null!;
        private UserCommentBlockViewModel _userCommentBlockViewModel = null!;
        private ObservableCollection<UserCommentBlockViewModel> _replies = [];

        public bool ReplyBoxExpanded
        {
            get => _replyBoxExpanded;
            set => SetProperty(ref _replyBoxExpanded, value);
        }
        public string ReplyText
        {
            get => _replyText;
            set => SetProperty(ref _replyText, value);
        }
        public string DiffHunk
        {
            get => _diffHunk;
            set => SetProperty(ref _diffHunk, value);
        }
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
        public User Author
        {
            get => _author;
            set => SetProperty(ref _author, value);
        }
        public ReviewCommentNode ReviewCommentNode
        {
            get => _reviewCommentNode;
            set => SetProperty(ref _reviewCommentNode, value);
        }

        public UserCommentBlockViewModel BodyViewModel
        {
            get => _userCommentBlockViewModel;
            set => SetProperty(ref _userCommentBlockViewModel, value);
        }

        public ObservableCollection<UserCommentBlockViewModel> Replies
        {
            get => _replies;
            set => SetProperty(ref _replies, value);
        }

        public ICommand ReplyCommand { get; }
        public ICommand ScrollToElementCommand { get; }

        public UIElement? ReplyBox { get; set; }

        public ReviewCommentViewModel(Repository repo, ReviewCommentNode comment, ICommand scrollToElement)
        {
            Repo = repo;
            DiffHunk = comment.DiffHunk ?? string.Empty;
            Name = comment.Path ?? string.Empty;
            Author = comment.User!;
            ReviewCommentNode = comment;
            _quoteReplyCommand = new RelayCommand<string?>(QuoteReply);
            BodyViewModel = new UserCommentBlockViewModel(comment, _quoteReplyCommand);
            foreach (var reply in comment.Replies)
            {
                Replies.Add(new UserCommentBlockViewModel(reply, _quoteReplyCommand));
            }
            ScrollToElementCommand = scrollToElement;
            ReplyCommand = new RelayCommand(OnReply);
        }

        private async void OnReply()
        {
            if (string.IsNullOrWhiteSpace(ReplyText)) return;
            var replyText = ReplyText.Trim();
            try
            {
                var reply = await GitHubService.ReplyToReview(Repo, ReviewCommentNode.Number, replyText, ReviewCommentNode.Id);
                ReplyText = "";
                var replyVM = new UserCommentBlockViewModel(reply, _quoteReplyCommand);
                Replies.Add(replyVM);
            }
            catch { }
        }

        private void ExpandReplyBox()
        {
            ReplyBoxExpanded = true;
        }

        private void QuoteReply(string? text)
        {
            ExpandReplyBox();
            var lines = (text ?? string.Empty).Split('\n')
                .Select((line) => $"> {line}\n");
            var builder = new StringBuilder();
            foreach (var line in lines)
            {
                builder.Append(line);
            }
            ReplyText = builder.ToString();
            if (ReplyBox is not null && ScrollToElementCommand.CanExecute(ReplyBox))
            {
                ScrollToElementCommand.Execute(ReplyBox);
            }
        }
    }
}


