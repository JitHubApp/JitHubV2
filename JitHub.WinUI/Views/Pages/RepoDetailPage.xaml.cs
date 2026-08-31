using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using JitHub.Models.LegacyGitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using JitHub.WinUI.ViewModels.RepositoryViewModels;
using Page = Microsoft.UI.Xaml.Controls.Page;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace JitHub.WinUI.Views.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class RepoDetailPage : Page, INotifyPropertyChanged
    {
        private bool _isCompact;
        private bool _syncingSectionSelection;
        private MainWindow? _mainWindow;
        private bool _shouldLoadRepositoryStatButtons;

        public InfoBarSeverity RepositoryActionStatusSeverity => ViewModel.ActionStatusKind switch
        {
            RepositoryActionStatusKind.Warning => InfoBarSeverity.Warning,
            RepositoryActionStatusKind.Error => InfoBarSeverity.Error,
            RepositoryActionStatusKind.Success => InfoBarSeverity.Success,
            _ => InfoBarSeverity.Informational
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool ShouldLoadRepositoryStatButtons
        {
            get => _shouldLoadRepositoryStatButtons;
            private set
            {
                if (_shouldLoadRepositoryStatButtons == value)
                {
                    return;
                }

                _shouldLoadRepositoryStatButtons = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShouldLoadRepositoryStatButtons)));
            }
        }

        private string ForkActionLabel =>
            LocalizedResourceText.GetString("RepoDetail.ForkAction", "Fork repository");

        public RepoDetailPage()
        {
            this.InitializeComponent();
            ProductPerformanceReadiness.RecordTraversalStage("repo_detail.xaml.ready");
            RepoDetailFrame.CacheSize = 4;
            RepoDetailFrame.Navigated += RepoDetailFrame_Navigated;
            RepoDetailFrame.NavigationFailed += RepoDetailFrame_NavigationFailed;
            ViewModel.RepositoryNavigationRequested += ViewModel_RepositoryNavigationRequested;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ((App)Application.Current).GetService<NavigationService>().RepoFrame = RepoDetailFrame;
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(ViewModel.ActionStatusKind), StringComparison.Ordinal))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RepositoryActionStatusSeverity)));
            }

            if (string.Equals(e.PropertyName, nameof(ViewModel.IsRepositoryIdentityVisible), StringComparison.Ordinal))
            {
                ApplyResponsiveChrome(ActualWidth);
            }
        }

        private void ViewModel_RepositoryNavigationRequested(object? sender, RepositoryNavigationRequest request)
        {
            Type pageType = request.Section switch
            {
                RepositoryWorkspaceSection.Code => typeof(RepoCodePage),
                RepositoryWorkspaceSection.Issues => typeof(RepoIssuePage),
                RepositoryWorkspaceSection.PullRequests => typeof(RepoPullRequestPage),
                RepositoryWorkspaceSection.Commits => typeof(RepoCommitsPage),
                _ => throw new ArgumentOutOfRangeException(nameof(request.Section))
            };

            if (!RepoDetailFrame.Navigate(pageType, request.Parameter, new SuppressNavigationTransitionInfo()))
            {
                ViewModel.ReportChildNavigationFailure(
                    new InvalidOperationException($"Navigation to {pageType.Name} was rejected."));
            }
        }

        private void RepoDetailFrame_Navigated(object sender, NavigationEventArgs e)
        {
            if (e.Content is Page page)
            {
                page.NavigationCacheMode = NavigationCacheMode.Required;
            }

            ViewModel.SetActiveSection(e.Content switch
            {
                RepoCodePage => RepositoryWorkspaceSection.Code,
                RepoIssuePage => RepositoryWorkspaceSection.Issues,
                RepoPullRequestPage => RepositoryWorkspaceSection.PullRequests,
                RepoCommitsPage => RepositoryWorkspaceSection.Commits,
                _ => ViewModel.SelectedSection
            });

            SyncSectionSelection();
        }

        private void RepoDetailFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            ViewModel.ReportChildNavigationFailure(e.Exception);
            e.Handled = true;
        }

        override protected void OnNavigatedTo(NavigationEventArgs e)
        {
            UiTaskGuard.Run(async () =>
            {
                base.OnNavigatedTo(e);
                ProductPerformanceReadiness.RecordTraversalStage("repo_detail.navigated");
                if (await TryReactivateCachedWorkspaceAsync(e.Parameter as RepoDetailPageArgs))
                {
                    SyncSectionSelection();
                    return;
                }

                if (e.Parameter is RepoDetailPageArgs args)
                {
                    await ViewModel.InitializeAsync(args);
                }
            }, "ui-repo-detail-page");
        }

        private async Task<bool> TryReactivateCachedWorkspaceAsync(RepoDetailPageArgs? args)
        {
            if (args?.Page != RepoPageType.PullRequestPage ||
                args.Ref is not PullRequestPageNavArg pullRequestArgs ||
                RepoDetailFrame.Content is not RepoPullRequestPage pullRequestPage ||
                pullRequestPage.ViewModel.PullRequests.Count == 0)
            {
                return false;
            }

            string requestedFullName = args.Repo?.FullName
                ?? args.FullName
                ?? pullRequestArgs.Repo.FullName;
            string currentFullName = pullRequestPage.ViewModel.NavigationArgs?.Repo.FullName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(requestedFullName) ||
                !string.Equals(requestedFullName, currentFullName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            pullRequestArgs.WithRepo(args.Repo ?? pullRequestArgs.Repo);
            await pullRequestPage.ViewModel.InitializeAsync(pullRequestArgs);
            ProductPerformanceReadiness.CommitRoute(
                "repo_pull_requests",
                $"{ProductPerformanceReadiness.CountIdentity(pullRequestPage.ViewModel.PullRequests.Count)};" +
                $"selected={pullRequestPage.ViewModel.SelectedPullRequest?.Id ?? 0}");
            ViewModel.SetActiveSection(RepositoryWorkspaceSection.PullRequests);
            return true;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ViewModel.CancelPendingOperations();
            base.OnNavigatedFrom(e);
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            MainWindow mainWindow = ((App)Application.Current).CurrentMainWindow;
            if (ReferenceEquals(_mainWindow, mainWindow))
            {
                return;
            }

            UnsubscribeFromWindowClose();
            _mainWindow = mainWindow;
            _mainWindow.ClosingRequested += MainWindow_ClosingRequested;
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.CancelPendingOperations();
            UnsubscribeFromWindowClose();
        }

        private void MainWindow_ClosingRequested(object? sender, EventArgs e) =>
            ViewModel.CancelPendingOperations();

        private void UnsubscribeFromWindowClose()
        {
            if (_mainWindow is null)
            {
                return;
            }

            _mainWindow.ClosingRequested -= MainWindow_ClosingRequested;
            _mainWindow = null;
        }

        private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyResponsiveChrome(e.NewSize.Width);
        }

        private void ApplyResponsiveChrome(double width)
        {
            bool compact = width < 760;
            bool condensed = width < 1180;
            bool hideIdentity = width < 980;
            _isCompact = compact;
            RepoDetailIdentityChrome.Visibility = compact || !hideIdentity
                ? ViewModel.IsRepositoryIdentityVisible ? Visibility.Visible : Visibility.Collapsed
                : Visibility.Collapsed;
            RepoDetailIdentityStatusBadge.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            RepoDetailSectionSelectorHost.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            RepoDetailBranchChrome.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ShouldLoadRepositoryStatButtons = !compact && !condensed;
            RepoDetailActionsMenuButton.Visibility = !compact && condensed
                ? Visibility.Visible
                : Visibility.Collapsed;
            RepoDetailCompactCommandsButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            if (!compact)
            {
                SyncSectionSelection();
            }
        }

        private void RepoDetailSectionSelector_SelectionChanged(
            SelectorBar sender,
            SelectorBarSelectionChangedEventArgs args)
        {
            if (_syncingSectionSelection || sender.SelectedItem is null)
            {
                return;
            }

            string? section = ReferenceEquals(sender.SelectedItem, RepoDetailCodeSectionItem)
                ? "code"
                : ReferenceEquals(sender.SelectedItem, RepoDetailIssuesSectionItem)
                    ? "issues"
                    : ReferenceEquals(sender.SelectedItem, RepoDetailPullRequestsSectionItem)
                        ? "pull-requests"
                        : ReferenceEquals(sender.SelectedItem, RepoDetailCommitsSectionItem)
                            ? "commits"
                            : null;
            if (section is null)
            {
                return;
            }

            ViewModel.NavigateToCompactSection(section);
            SyncSectionSelection();
        }

        private void SyncSectionSelection()
        {
            SelectorBarItem? item = ViewModel.SelectedSection switch
            {
                RepositoryWorkspaceSection.Code => RepoDetailCodeSectionItem,
                RepositoryWorkspaceSection.Issues => RepoDetailIssuesSectionItem,
                RepositoryWorkspaceSection.PullRequests => RepoDetailPullRequestsSectionItem,
                RepositoryWorkspaceSection.Commits => RepoDetailCommitsSectionItem,
                _ => null
            };
            if (item is null || ReferenceEquals(RepoDetailSectionSelector.SelectedItem, item))
            {
                return;
            }

            _syncingSectionSelection = true;
            RepoDetailSectionSelector.SelectedItem = item;
            _syncingSectionSelection = false;
        }

        private void RepoDetailCompactCommandsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isCompact)
            {
                return;
            }

            MenuFlyout flyout = BuildCompactCommandsFlyout();
            flyout.ShowAt(RepoDetailCompactCommandsButton);
        }

        private MenuFlyout BuildCompactCommandsFlyout()
        {
            MenuFlyout flyout = new();
            MenuFlyoutItem identity = new()
            {
                Text = string.IsNullOrWhiteSpace(ViewModel.RepositoryFullName)
                    ? T("RepoDetail/Compact/Repository", "Repository")
                    : ViewModel.RepositoryFullName,
                IsEnabled = false
            };
            AutomationProperties.SetAutomationId(identity, "RepoDetailCompactRepositoryIdentity");
            AutomationProperties.SetName(identity, identity.Text);
            flyout.Items.Add(identity);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(CreateSectionItem("RepoDetailCompactSectionCode", T("RepoDetail/Compact/Code", "Code"), "code", ViewModel.SelectedSection == RepositoryWorkspaceSection.Code));
            flyout.Items.Add(CreateSectionItem("RepoDetailCompactSectionIssues", T("RepoDetail/Compact/Issues", "Issues"), "issues", ViewModel.SelectedSection == RepositoryWorkspaceSection.Issues));
            flyout.Items.Add(CreateSectionItem("RepoDetailCompactSectionPullRequests", T("RepoDetail/Compact/PullRequests", "Pull requests"), "pull-requests", ViewModel.SelectedSection == RepositoryWorkspaceSection.PullRequests));
            flyout.Items.Add(CreateSectionItem("RepoDetailCompactSectionCommits", T("RepoDetail/Compact/Commits", "Commits"), "commits", ViewModel.SelectedSection == RepositoryWorkspaceSection.Commits));

            bool isCodeSurface =
                ViewModel.SelectedSection == RepositoryWorkspaceSection.Code ||
                RepoDetailFrame.Content is RepoCodePage;
            if (isCodeSurface)
            {
                MenuFlyoutItem branches = new()
                {
                    Text = TF("RepoDetail/Compact/BranchFormat", "Branch: {0}", ViewModel.SelectedBranchName),
                    IsEnabled = true
                };
                AutomationProperties.SetAutomationId(branches, "RepoDetailCompactBranchMenu");
                AutomationProperties.SetName(branches, branches.Text);
                branches.Click += (_, _) => DispatcherQueue.TryEnqueue(() =>
                {
                    ViewModel.BranchSearchText = string.Empty;
                    BranchPickerFlyout.ShowAt(RepoDetailCompactCommandsButton);
                });

                flyout.Items.Add(branches);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(CreateCommandItem(
                "RepoDetailCompactWatch",
                ViewModel.WatchActionLabel,
                ViewModel.ToggleWatchCommand,
                ViewModel.CanToggleWatch));
            flyout.Items.Add(CreateCommandItem(
                "RepoDetailCompactStar",
                ViewModel.StarActionLabel,
                ViewModel.ToggleStarCommand,
                ViewModel.CanToggleStar));
            flyout.Items.Add(CreateCommandItem(
                "RepoDetailCompactFork",
                ForkActionLabel,
                ViewModel.ForkCommand,
                ViewModel.CanForkRepository));

            if (RepoDetailFrame.Content is IRepositoryCompactCommandProvider provider)
            {
                IReadOnlyList<RepositoryCompactCommand> childCommands = provider.GetRepositoryCompactCommands();
                if (childCommands.Count > 0)
                {
                    flyout.Items.Add(new MenuFlyoutSeparator());
                    foreach (RepositoryCompactCommand command in childCommands)
                    {
                        MenuFlyoutItem item = new()
                        {
                            Text = command.Label,
                            IsEnabled = command.IsEnabled
                        };
                        AutomationProperties.SetAutomationId(
                            item,
                            $"RepoDetailCompactChild_{SanitizeAutomationId(command.Id)}");
                        AutomationProperties.SetName(item, command.Label);
                        item.Click += (_, _) => command.Execute();
                        flyout.Items.Add(item);
                    }
                }
            }

            return flyout;
        }

        private void RepoDetailBranchSearchBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is AutoSuggestBox searchBox)
            {
                searchBox.Focus(FocusState.Programmatic);
            }
        }

        private void RepoDetailBranchList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not Branch branch)
            {
                return;
            }

            ViewModel.SelectCompactBranch(branch);
            BranchPickerFlyout.Hide();
        }

        private void RepoDetailBranchList_ContainerContentChanging(
            ListViewBase sender,
            ContainerContentChangingEventArgs args)
        {
            if (args.ItemContainer is not null && args.Item is Branch branch)
            {
                AutomationProperties.SetAutomationId(
                    args.ItemContainer,
                    $"RepoDetailBranch_{SanitizeAutomationId(branch.Name)}");
                AutomationProperties.SetName(
                    args.ItemContainer,
                    TF("RepoDetail/Compact/OpenBranchFormat", "Open branch {0}", branch.Name));
            }
        }

        private ToggleMenuFlyoutItem CreateSectionItem(
            string automationId,
            string text,
            string section,
            bool selected)
        {
            ToggleMenuFlyoutItem item = new()
            {
                Text = text,
                IsChecked = selected,
                IsEnabled = !ViewModel.Loading
            };
            AutomationProperties.SetAutomationId(item, automationId);
            AutomationProperties.SetName(item, text);
            item.Click += (_, _) => ViewModel.NavigateToCompactSection(section);
            return item;
        }

        private static MenuFlyoutItem CreateCommandItem(
            string automationId,
            string text,
            System.Windows.Input.ICommand command,
            bool isEnabled)
        {
            MenuFlyoutItem item = new()
            {
                Text = text,
                Command = command,
                IsEnabled = isEnabled
            };
            AutomationProperties.SetAutomationId(item, automationId);
            AutomationProperties.SetName(item, text);
            return item;
        }

        private static string SanitizeAutomationId(string value) =>
            string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '_'));

        private static string T(string key, string fallback) =>
            LocalizedResourceText.GetString(key, fallback);

        private static string TF(string key, string fallback, params object?[] arguments) =>
            LocalizedResourceText.Format(key, fallback, arguments);
    }
}

