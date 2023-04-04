using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Views;
using JitHub.Views.Pages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Toolkit.Uwp;
using Microsoft.Toolkit.Uwp.UI;
using Microsoft.UI.Xaml.Controls;
using Octokit;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel.Store;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.ApplicationModel.Core;
using JitHub.Views.Controls.Common;
using JitHub.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Xaml.Media.Animation;

namespace JitHub.ViewModels
{
    public class ShellViewModel : ObservableObject
    {
        private const string StorePage = "https://www.microsoft.com/store/apps/9MXRBJBB552V";
        private DispatcherQueueTimer _timer;
        private DispatcherQueue _queue;
        private NavigationService _navigationService;
        private IGitHubService _gitHubSerivce;
        private IAuthService _authService;
        private ModalService _modalService;
        private FrameworkElement _content;
        private string _title;
        private bool _useHeader;
        private ObservableCollection<TabViewItem> _pages = new ObservableCollection<TabViewItem>();
        private TabViewItem _selectedTab;
        private ICollection<Repository> _searchResults;
        private int _slideUpPanelHeight;
        private bool _searching;

        public int SlideUpPanelHeight
        {
            get => _slideUpPanelHeight;
            set => SetProperty(ref _slideUpPanelHeight, value);
        }
        public FrameworkElement Content
        {
            get => _content;
            set => SetProperty(ref _content, value);
        }
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }
        public bool UseHeader
        {
            get => _useHeader;
            set => SetProperty(ref _useHeader, value);
        }
        public ObservableCollection<TabViewItem> Pages
        {
            get => _pages;
            set => SetProperty(ref _pages, value);
        }
        public TabViewItem SelectedTab
        {
            get => _selectedTab;
            set => SetProperty(ref _selectedTab, value);
        }
        public ICollection<Repository> SearchResults
        {
            get => _searchResults;
            set => SetProperty(ref _searchResults, value);
        }
        public bool Searching
        {
            get => _searching;
            set => SetProperty(ref _searching, value);
        }

        public GlobalViewModel GlobalViewModel { get; }
        public ICommand GoHomeCommand;
        public ICommand OpenModalCommand { get; set; }
        public ICommand CloseModalCommand { get; set; }
        public ICommand OpenModalWithControlCommand { get; set; }
        public ICommand CloseModalWithControlCommand { get; set; }
        public ICommand InLineModalClodeCommand { get; set; }
        public ICommand GoToProfilePageCommand { get; set; }

        public FeatureService FeatureService;
        public ShellViewModel()
        {
            _navigationService = Ioc.Default.GetService<NavigationService>();
            _gitHubSerivce = Ioc.Default.GetService<IGitHubService>();
            _authService = Ioc.Default.GetService<IAuthService>();
            FeatureService = Ioc.Default.GetService<FeatureService>();
            GlobalViewModel = Ioc.Default.GetService<GlobalViewModel>();
            _modalService = Ioc.Default.GetService<ModalService>();
            GoHomeCommand = new RelayCommand(GoHome);
            GoToProfilePageCommand = new RelayCommand(GoToProfilePage);
            _navigationService.RegisterTabTitleChangeEvent(new RelayCommand<string>(ChangeTabTitle));
            var _queueController = DispatcherQueueController.CreateOnDedicatedThread();
            _queue = _queueController.DispatcherQueue;
            _timer = _queue.CreateTimer();
            var dt = DataTransferManager.GetForCurrentView();
            dt.DataRequested += OnDataRequested;
        }

        private void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            DataRequest request = args.Request;
            request.Data.SetWebLink(new Uri(StorePage));
            request.Data.Properties.Title = StorePage;
            request.Data.Properties.Description = "JitHub";
        }

        public void LoadApplication(ICommand openModal, ICommand closeModal)
        {
            //order here is very important
            OpenModalCommand = openModal;
            CloseModalCommand = closeModal;
            OpenModalWithControlCommand = new RelayCommand<ModalArg>(OpenModalWithControl);
            CloseModalWithControlCommand = new RelayCommand(CloseModalWithControl);
            _modalService.Init(OpenModalWithControlCommand, CloseModalWithControlCommand);
            InLineModalClodeCommand = new RelayCommand(CloseModal);
            if (!_authService.Authenticated)
            {
                _navigationService.Unauthorized();
            }
            else
            {
                OpenTab("Home", typeof(DashboardPage));
            }
        }

        private void ChangeTabTitle(string title)
        {
            if (SelectedTab != null)
            {
                SelectedTab.Header = title;
            }
        }

        private void GoHome(Frame frame)
        {
            frame.Navigate(typeof(DashboardPage), null, new SuppressNavigationTransitionInfo());
        }

        public void OnAddTab(TabView sender, object args)
        {
            if (FeatureService.ProLicense || Pages.Count == 0)
            {
                OpenTab("Home", typeof(DashboardPage));
            }
            else
            {
                var buyCommand = new AsyncRelayCommand(BuyProFeature);
                var cancelCommand = new RelayCommand(CloseModal);
                _modalService.Open(new FeaturePurchaseDialog(buyCommand, cancelCommand));
            }
        }

        private async Task BuyProFeature()
        {
            var res = await FeatureService.BuyProFeature();
            _modalService.Close();
            switch (res)
            {
                // TODO: Add reactions to all these cases
                case FeaturePurchaseState.Success:
                    _modalService.Open("Thank you!", new ProLicensePurchaseSuccessDialog(new RelayCommand(CloseModal)));
                    break;
                case FeaturePurchaseState.Failure:
                    break;
                case FeaturePurchaseState.AlreadyOwn:
                    break;
                default:
                    break;
            }
        }

        public void OnShareJitHub(object sender, RoutedEventArgs e)
        {
            DataTransferManager.ShowShareUI();
        }

        public void OnSignOut(object sender, RoutedEventArgs e)
        {
            _authService.SignOut();
        }

        public void OnOpenDevConsole(object sender, RoutedEventArgs e)
        {
            _modalService.Open("Dev Console", new DevConsole());
        }

        public void OnTabClose(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (FeatureService.ProLicense || Pages.Count == 1)
            {
                try
                {
                    var item = args.Item as TabViewItem;
                    Pages.Remove(item);
                }
                catch (Exception) { }
            }
            else
            {
                var buyCommand = new AsyncRelayCommand(BuyProFeature);
                var cancelCommand = new RelayCommand(CloseModal);
                _modalService.Open(new FeaturePurchaseDialog(buyCommand, cancelCommand));
            }
        }

        public void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                _navigationService.ApplicationFrame = null;
                if (e.AddedItems.Count > 0)
                {
                    var tabItem = (TabViewItem)e.AddedItems[0];
                    var frame = (Frame)tabItem.Content;
                    _navigationService.ApplicationFrame = frame;
                }
            }
            catch (Exception) { }
        }

        private void OpenTab(string header, Type pageSource)
        {
            if (FeatureService.ProLicense || Pages.Count == 0)
            {
                var frame = new Frame();
                var tab = new TabViewItem()
                {
                    Header = header,
                    Content = frame
                };
                if (pageSource == typeof(DashboardPage))
                {
                    frame.Navigate(pageSource, null, new SuppressNavigationTransitionInfo());
                }
                else
                {
                    frame.Navigate(pageSource);
                }
                Pages.Add(tab);
                SelectedTab = tab;
            }
            else
            {
                _navigationService.NavigateTo(header, pageSource);
            }
        }

        private void OpenTab(string header, Type pageSource, object param)
        {
            if (FeatureService.ProLicense || Pages.Count == 0)
            {
                var frame = new Frame();
                var tab = new TabViewItem()
                {
                    Header = header,
                    Content = frame
                };
                frame.Navigate(pageSource, param);
                Pages.Add(tab);
                SelectedTab = tab;
            }
            else
            {
                _navigationService.NavigateTo(header, pageSource, param);
            }
        }

        public void GoHome()
        {
            if (SelectedTab != null && SelectedTab?.Content != null)
            {
                var frame = (Frame)SelectedTab.Content;
                GoHome(frame);
            }
            else
            {
                OpenTab("Home", typeof(DashboardPage));
            }
            ChangeTabTitle("Home");
        }

        public void GoToFeedbackPage()
        {
            var repoName = "nerocui/JitHubFeedback";
            var param = new RepoDetailPageArgs(RepoPageType.IssuePage, new IssueNavArg(null, 0), repoName);
            OpenTab(repoName, typeof(RepoDetailPage), param);
        }

        public void GoToSettingsPage()
        {
            var existing = Pages.FirstOrDefault(page => (string)page.Header == "Settings");
            if (existing == null)
            {
                OpenTab("Settings", typeof(SettingsPage));
            }
            else
            {
                SelectedTab = existing;
            }
        }

        public void GoToProfilePage()
        {
            var existing = Pages.FirstOrDefault(page => (string)page.Header == "User Profile");
            if (existing == null)
            {
                OpenTab("User Profile", typeof(ProfilePage));
            }
            else
            {
                SelectedTab = existing;
            }
        }

        public void OpenRepo(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (args.ChosenSuggestion == null && !string.IsNullOrWhiteSpace(args.QueryText))
            {
                sender.Text = args.QueryText;
                OpenTab($"Search results for {args.QueryText}", typeof(RepoSearchResultPage), args.QueryText);
            }
            else
            {
                var repo = args.ChosenSuggestion as Repository;
                if (repo != null)
                {
                    var header = string.IsNullOrWhiteSpace(repo.FullName) ? repo.Name : repo.FullName;
                    sender.Text = header;
                    var param = new RepoDetailPageArgs(RepoPageType.CodePage, repo);
                    OpenTab(header, typeof(RepoDetailPage), param);
                }
            }
            _timer.Stop();
        }

        public void OpenModalWithControl(ModalArg arg)
        {
            Content = arg.Content;
            Title = arg.Title;
            UseHeader = arg.UseHeader;
            if (OpenModalCommand != null && OpenModalCommand.CanExecute(null))
            {
                OpenModalCommand.Execute(null);
            }
        }

        public void CloseModalWithControl()
        {
            if (CloseModalCommand != null && CloseModalCommand.CanExecute(null))
            {
                CloseModalCommand.Execute(null);
            }
            Content = null;
            Title = string.Empty;
        }

        private void CloseModal()
        {
            _modalService?.Close();
        }

        public void OnTextChange(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                var term = sender.Text.Trim();
                if (string.IsNullOrWhiteSpace(term))
                {
                    SearchResults = new List<Repository>();
                    return;
                }
                var searchRequest = new SearchRepositoriesRequest(term);
                _timer.Debounce(async () =>
                {
                    await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(() =>
                    {
                        Searching = true;
                    });
                    var result = await _gitHubSerivce.GitHubClient.Search.SearchRepo(searchRequest);
                    await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(() =>
                    {
                        SearchResults = result.Items.ToList();
                        Searching = false;
                    });
                }, TimeSpan.FromMilliseconds(200));
            }
        }

        public void SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            try
            {
                var repo = (Repository)args.SelectedItem;
                if (repo != null)
                {
                    sender.Text = string.IsNullOrWhiteSpace(repo.FullName) ? repo.Name : repo.FullName;
                }
            }
            catch (Exception) { }
            _timer.Stop();
        }

        public async Task OnNavigatedTo()
        {
            await FeatureService.SetLicenseStatus();
        }
    }
}
