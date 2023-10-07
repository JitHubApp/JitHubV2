using JitHub.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using Octokit;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace JitHub.Models.Base
{
    public class RepoSelectableItemModel<T> : ObservableObject
    {
        private T _model;
        private Repository _repo;
        private bool _selected = false;

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
