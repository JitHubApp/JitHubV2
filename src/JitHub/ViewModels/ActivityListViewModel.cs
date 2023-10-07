using JitHub.Models;
using JitHub.Services;
using JitHub.ViewModels.ActivityViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Toolkit.Uwp;
using System.Collections.Specialized;
using Windows.UI.Xaml;

namespace JitHub.ViewModels
{
    public partial class ActivityListViewModel : ObservableObject
    {
        private bool _loading;
        private IGitHubService _gitHubService;
        private IncrementalLoadingCollection<ActivitySource, ActivityViewModel> _activities;
        [ObservableProperty]
        private Visibility _activitiesVisible;
        [ObservableProperty]
        private Visibility _emptyVisible;

        public bool Loading
        {
            get => _loading;
            set => SetProperty(ref _loading, value);
        }

        public IncrementalLoadingCollection<ActivitySource, ActivityViewModel> Activities
        {
            get => _activities;
            set => SetProperty(ref _activities, value);
        }

        public ActivityListViewModel()
        {
            _gitHubService = Ioc.Default.GetService<IGitHubService>();
            var loadCommand = new RelayCommand(() => Loading = true);
            var finishCommand = new RelayCommand(() => Loading = false);
            var activitySource = new ActivitySource(loadCommand, finishCommand);
            Activities = new IncrementalLoadingCollection<ActivitySource, ActivityViewModel>(activitySource);
            Activities.RefreshAsync();
            Activities.CollectionChanged += OnActivitiesChanges;
        }

        private void OnActivitiesChanges(object sender, NotifyCollectionChangedEventArgs e)
        {
            var activities = (IncrementalLoadingCollection<ActivitySource, ActivityViewModel>)sender;
            if (activities.Count == 0 )
            {
                ActivitiesVisible = Visibility.Collapsed;
                EmptyVisible = Visibility.Visible;
            }
            else
            {
                ActivitiesVisible = Visibility.Visible;
                EmptyVisible = Visibility.Collapsed;
            }
        }
    }
}
