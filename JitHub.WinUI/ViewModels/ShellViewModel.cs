using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.Views;
using JitHub.WinUI.Views.Controls.Common;
using JitHub.WinUI.Views.Pages;
using DashboardPageType = JitHub.WinUI.Views.Pages.DashboardPage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using JitHub.Models;
using JitHub.Models.GitHub;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace JitHub.WinUI.ViewModels
{
    public class ShellViewModel : ObservableObject
    {
        private const string StorePage = "https://www.microsoft.com/store/apps/9MXRBJBB552V";
        private readonly NavigationService _navigationService;
        private readonly IGitHubClientService _gitHubClientService;
        private readonly IAccountService _accountService;
        private readonly IAuthService _authService;
        private readonly ModalService _modalService;
        private DataTransferManager? _shareManager;
        private Window? _shareWindow;
        private FrameworkElement? _content;
        private string _title = string.Empty;
        private bool _useHeader;
        private ObservableCollection<TabViewItem> _pages = [];
        private TabViewItem? _selectedTab;
        private ICollection<GitHubRepository> _searchResults = [];
        private int _slideUpPanelHeight;
        private bool _searching;
        private IDisposable? _searchSubscription;

        public int SlideUpPanelHeight
        {
            get => _slideUpPanelHeight;
            set => SetProperty(ref _slideUpPanelHeight, value);
        }
        public FrameworkElement? Content
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
        public TabViewItem? SelectedTab
        {
            get => _selectedTab;
            set => SetProperty(ref _selectedTab, value);
        }
        public ICollection<GitHubRepository> SearchResults
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
        public ICommand GoHomeCommand { get; }
        public ICommand? OpenModalCommand { get; private set; }
        public ICommand? CloseModalCommand { get; private set; }
        public ICommand? OpenModalWithControlCommand { get; private set; }
        public ICommand? CloseModalWithControlCommand { get; private set; }
        public ICommand? InLineModalClodeCommand { get; private set; }
        public ICommand GoToProfilePageCommand { get; }

        public FeatureService FeatureService { get; }
        public ShellViewModel()
        {
            _navigationService = Ioc.Default.GetService<NavigationService>()
                ?? throw new InvalidOperationException("NavigationService is not registered.");
            _gitHubClientService = Ioc.Default.GetService<IGitHubClientService>()
                ?? throw new InvalidOperationException("IGitHubClientService is not registered.");
            _accountService = Ioc.Default.GetService<IAccountService>()
                ?? throw new InvalidOperationException("IAccountService is not registered.");
            _authService = Ioc.Default.GetService<IAuthService>()
                ?? throw new InvalidOperationException("IAuthService is not registered.");
            FeatureService = Ioc.Default.GetService<FeatureService>()
                ?? throw new InvalidOperationException("FeatureService is not registered.");
            GlobalViewModel = Ioc.Default.GetService<GlobalViewModel>()
                ?? throw new InvalidOperationException("GlobalViewModel is not registered.");
            _modalService = Ioc.Default.GetService<ModalService>()
                ?? throw new InvalidOperationException("ModalService is not registered.");
            GoHomeCommand = new RelayCommand(GoHome);
            GoToProfilePageCommand = new RelayCommand(GoToProfilePage);
            _navigationService.RegisterTabTitleChangeEvent(new RelayCommand<string?>(ChangeTabTitle));
        }

        public void InitializeDesktopIntegration(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);

            if (ReferenceEquals(_shareWindow, window))
            {
                return;
            }

            if (_shareManager is not null)
            {
                _shareManager.DataRequested -= OnDataRequested;
            }

            _shareWindow = window;
        }

        private void EnsureShareManager()
        {
            if (_shareWindow is null)
            {
                throw new InvalidOperationException("Share UI is not initialized.");
            }

            if (_shareManager is not null)
            {
                return;
            }

            _shareManager = DesktopDataTransferManagerHelper.GetForWindow(_shareWindow);
            _shareManager.DataRequested += OnDataRequested;
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
            OpenModalWithControlCommand = new RelayCommand<ModalArg?>(OpenModalWithControl);
            CloseModalWithControlCommand = new RelayCommand(CloseModalWithControl);
            _modalService.Init(OpenModalWithControlCommand, CloseModalWithControlCommand);
            InLineModalClodeCommand = new RelayCommand(CloseModal);
            if (!_authService.Authenticated && !_authService.CheckAuth(_accountService.GetUser()))
            {
                _navigationService.Unauthorized();
            }
            else
            {
                OpenTab("Home", typeof(DashboardPageType));
            }
        }

        private void ChangeTabTitle(string? title)
        {
            if (SelectedTab is not null && title is not null)
            {
                SelectedTab.Header = title;
            }
        }

        private void GoHome(Frame frame)
        {
            frame.Navigate(typeof(DashboardPageType), null, new SuppressNavigationTransitionInfo());
        }

        public void OnAddTab(TabView sender, object args)
        {
            if (FeatureService.ProLicense || Pages.Count == 0)
            {
                OpenTab("Home", typeof(DashboardPageType));
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
            if (_shareWindow is null)
            {
                throw new InvalidOperationException("Share UI is not initialized.");
            }

            EnsureShareManager();
            DesktopDataTransferManagerHelper.ShowShareUIForWindow(_shareWindow);
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
                if (args.Item is TabViewItem item)
                {
                    Pages.Remove(item);
                }
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
            _navigationService.ApplicationFrame = null;
            if (e.AddedItems.FirstOrDefault() is TabViewItem { Content: Frame frame })
            {
                _navigationService.ApplicationFrame = frame;
            }
        }

        private void OpenTab(string header, Type pageSource)
        {
            if (FeatureService.ProLicense || Pages.Count == 0)
            {
                var frame = new Frame();
                frame.ContentTransitions = null;
                var tab = new TabViewItem()
                {
                    Header = header,
                    Content = frame
                };
                frame.Navigate(pageSource, null, new SuppressNavigationTransitionInfo());
                Pages.Add(tab);
                SelectedTab = tab;
            }
            else
            {
                _navigationService.NavigateTo(header, pageSource);
            }
        }

        private void OpenTab(string header, Type pageSource, object? param)
        {
            if (FeatureService.ProLicense || Pages.Count == 0)
            {
                var frame = new Frame();
                frame.ContentTransitions = null;
                var tab = new TabViewItem()
                {
                    Header = header,
                    Content = frame
                };
                frame.Navigate(pageSource, param, new SuppressNavigationTransitionInfo());
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
            if (SelectedTab?.Content is Frame frame)
            {
                GoHome(frame);
            }
            else
            {
                OpenTab("Home", typeof(DashboardPageType));
            }
            ChangeTabTitle("Home");
        }

        public void GoToFeedbackPage()
        {
            var repoName = "JitHubApp/JitHubV2";
            var param = new RepoDetailPageArgs(RepoPageType.IssuePage, new IssueNavArg((GitHubRepository?)null, 0), repoName);
            OpenTab(repoName, typeof(RepoDetailPage), param);
        }

        public void GoToSettingsPage()
        {
            var existing = Pages.FirstOrDefault(page => string.Equals(page.Header as string, "Settings", StringComparison.Ordinal));
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
            var existing = Pages.FirstOrDefault(page => string.Equals(page.Header as string, "User Profile", StringComparison.Ordinal));
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
                var repo = args.ChosenSuggestion as GitHubRepository;
                if (repo != null)
                {
                    var header = string.IsNullOrWhiteSpace(repo.FullName) ? repo.Name : repo.FullName;
                    sender.Text = header;
                    var param = new RepoDetailPageArgs(RepoPageType.CodePage, repo);
                    OpenTab(header, typeof(RepoDetailPage), param);
                }
            }
        }

        public void OpenModalWithControl(ModalArg? arg)
        {
            if (arg is null)
            {
                return;
            }

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
            if (CloseModalCommand is not null && CloseModalCommand.CanExecute(null))
            {
                CloseModalCommand.Execute(null);
            }
            Content = null;
            Title = string.Empty;
            UseHeader = false;
        }

        private void CloseModal()
        {
            _modalService.Close();
        }

        public void RegisterSearchDebounce(AutoSuggestBox autoSearchBox)
        {
            ArgumentNullException.ThrowIfNull(autoSearchBox);

            var dispatcherQueue = autoSearchBox.DispatcherQueue;
            _searchSubscription?.Dispose();
            _searchSubscription = Observable
            .FromEventPattern<TypedEventHandler<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs>, AutoSuggestBoxTextChangedEventArgs>
            (
                s => autoSearchBox.TextChanged += s,
                s => autoSearchBox.TextChanged -= s
            )
            // Capture the plain text while we're still on the XAML thread.
            .Where(result => result.EventArgs.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            .Select(result => (result.Sender as AutoSuggestBox)?.Text ?? string.Empty)
            .Throttle(TimeSpan.FromMilliseconds(400))
            .DistinctUntilChanged()
            .Subscribe(text =>
            {
                if (dispatcherQueue.HasThreadAccess)
                {
                    _ = SearchAsync(text);
                    return;
                }

                _ = dispatcherQueue.TryEnqueue(() => _ = SearchAsync(text));
            });
        }

        private async Task SearchAsync(string text)
        {
            var term = text.Trim();
            if (string.IsNullOrWhiteSpace(term))
            {
                SearchResults = [];
                return;
            }

            string? token = _authService.GetToken(_accountService.GetUser());
            if (string.IsNullOrWhiteSpace(token))
            {
                SearchResults = [];
                Searching = false;
                _authService.SignOut();
                return;
            }

            Searching = true;
            try
            {
                SearchResults = (await _gitHubClientService.SearchRepositoriesAsync(token, term, 8)).ToList();
            }
            catch (GitHubAuthenticationException)
            {
                SearchResults = [];
                _authService.SignOut();
            }
            catch (GitHubApiException)
            {
                SearchResults = [];
            }
            catch (System.Net.Http.HttpRequestException)
            {
                SearchResults = [];
            }
            finally
            {
                Searching = false;
            }
        }

        public void SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (args.SelectedItem is GitHubRepository repo)
            {
                sender.Text = string.IsNullOrWhiteSpace(repo.FullName) ? repo.Name : repo.FullName;
            }
        }

        public async Task OnNavigatedTo()
        {
            await FeatureService.SetLicenseStatus();
        }
    }
}





