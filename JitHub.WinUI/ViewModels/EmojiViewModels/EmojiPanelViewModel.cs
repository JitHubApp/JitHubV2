using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.LegacyGitHub;
using System.Collections.Generic;
using System.Windows.Input;

namespace JitHub.WinUI.ViewModels.EmojiViewModels
{   
    public class EmojiPanelViewModel : ObservableObject
    {
        private Dictionary<ReactionType, Reaction> _votesMap = [];
        private Dictionary<ReactionType, ICollection<string>> _userReactions = [];

        public Dictionary<ReactionType, Reaction> VotesMap
        {
            get => _votesMap;
            set => SetProperty(ref _votesMap, value);
        }
        public Dictionary<ReactionType, ICollection<string>> UserReactions
        {
            get => _userReactions;
            set => SetProperty(ref _userReactions, value);
        }
        public ICommand? ReactionCommand { get; set; }
    }
}

