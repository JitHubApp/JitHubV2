using JitHub.Models;
using JitHub.WinUI.ViewModels.Base;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace JitHub.WinUI.ViewModels.IssueViewModels
{
    public class ReactionBlockViewModel : RepoViewModel
    {
        private MenuItem _copyLinkMenuItem = null!;
        private MenuItem _quoteReplyMenuItem = null!;
        private Dictionary<ReactionType, ICollection<string>> _userReactions = new Dictionary<ReactionType, ICollection<string>>();
        private Dictionary<ReactionType, bool> _votesMap = new Dictionary<ReactionType, bool>();
        //needs to take a ReactionType as an parameter
        public ICommand ReactionCommand { get; }
        public ICommand RemoveReactionCommand { get; }

        public MenuItem CopyLinkMenuItem
        {
            get => _copyLinkMenuItem;
            set => SetProperty(ref _copyLinkMenuItem, value);
        }
        public MenuItem QuoteReplyMenuItem
        {
            get => _quoteReplyMenuItem;
            set => SetProperty(ref _quoteReplyMenuItem, value);
        }

        public Dictionary<ReactionType, ICollection<string>> UserReactions
        {
            get => _userReactions;
            set => SetProperty(ref _userReactions, value);
        }

        public Dictionary<ReactionType, bool > VotesMap
        {
            get => _votesMap;
            set => SetProperty(ref _votesMap, value);
        }

        public ReactionBlockViewModel(MenuItem copyLink, MenuItem quoteReply, ICommand reactionCommand, ICommand reactionDeleteCommand)
        {
            CopyLinkMenuItem = copyLink;
            QuoteReplyMenuItem = quoteReply;
            //RemoveReactionCommand = new AsyncRelayCommand<int>(GitHubService.DeleteReaction);
            RemoveReactionCommand = reactionDeleteCommand;
            ReactionCommand = reactionCommand;
        }

        public void SetReactions(ICollection<Reaction> reactions)
        {
            var userReactions = new Dictionary<ReactionType, ICollection<string>>();
            var votesMap = new Dictionary<ReactionType, bool>();
            string currentUserLogin = User?.Login ?? string.Empty;
            foreach (var reaction in reactions)
            {
                string reactionUserLogin = reaction.User?.Login ?? string.Empty;
                if (!userReactions.ContainsKey(reaction.Content.Value))
                {
                    userReactions.Add(reaction.Content.Value, new List<string> { reactionUserLogin });
                }
                else
                {
                    userReactions[reaction.Content.Value].Add(reactionUserLogin);
                }

                if (!votesMap.ContainsKey(reaction.Content.Value))
                {
                    votesMap.Add(reaction.Content.Value, string.Equals(reactionUserLogin, currentUserLogin));
                }
                else if (!votesMap[reaction.Content.Value] && string.Equals(reactionUserLogin, currentUserLogin))
                {
                    votesMap[reaction.Content.Value] = true;
                }
            }

            UserReactions = userReactions;
            VotesMap = votesMap;
        }
    }
}


