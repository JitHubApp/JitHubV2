using JitHub.WinUI.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using Octokit;

namespace JitHub.Models.Base
{
    public class RepoSelectableItemModel<T> : ObservableObject
    {
        private T _model = default!;
        private Repository _repo = null!;
        private bool _selected;

        public T Model
        {
            get => _model;
            set => SetProperty(ref _model, value);
        }
        public Repository Repository
        {
            get => _repo;
            set => SetProperty(ref _repo, value);
        }
        public bool Selected
        {
            get => _selected;
            set
            {
                SetProperty(ref _selected, value);
            }
        }

        public RepoSelectableItemModel<T> Item => this;

        public RepoSelectableItemModel()
        {
            Selected = false;
        }
    }
}

