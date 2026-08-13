using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Services.Layout;
using JitHub.Services.Markdown;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.Performance;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Controls.Common;
using JitHub.WinUI.Views.Dialogs;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class RepoPullRequestPage : Page
{
    private const string ReplyIdentityAutomationScenario = "pr-reply-identities";
    private bool _initialized;
    private bool _openedInitialListDrawer;
    private CancellationTokenSource? _searchDebounce;
    private ProductPerformanceScrollProbe? _performanceScrollProbe;
    private int _selectionRenderGeneration;
    private bool _pointerSelectionInProgress;
    private int? _pendingPointerHydrationNumber;

    public RepoPullRequestPageViewModel ViewModel { get; }

    public RepoPullRequestPage()
    {
        ViewModel = ((App)Application.Current).GetService<RepoPullRequestPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        PullRequestContentScrollViewer.Loaded += PullRequestContentScrollViewer_Loaded;
        PullRequestContentScrollViewer.Unloaded += PullRequestContentScrollViewer_Unloaded;
        PullRequestDetailHost.SizeChanged += PullRequestDetailHost_SizeChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _initialized = false;
        _openedInitialListDrawer = false;
        PullRequestPageNavArg? arg = e.Parameter as PullRequestPageNavArg;
        bool isReplyIdentityAutomationScenario = string.Equals(
            Program.CurrentLaunchOptions.Scenario,
            ReplyIdentityAutomationScenario,
            StringComparison.OrdinalIgnoreCase);
        if (isReplyIdentityAutomationScenario)
        {
            PullRequestSectionSegmented.SelectedIndex = 3;
            PullRequestSectionComboBox.SelectedIndex = 3;
            ViewModel.SetSection(PullRequestWorkspaceSection.Reviews);
        }

        await ViewModel.InitializeAsync(arg);
        if (DialogMatrixAutomationScenario.IsEnabled)
        {
            bool hasSelection = ViewModel.SelectedPullRequest is not null;
            ViewModel.CanEditPullRequest = hasSelection;
            ViewModel.CanManagePullRequestMetadata = hasSelection;
            ViewModel.CanReactToPullRequest = hasSelection;
            ViewModel.CanSubmitReviewComment = hasSelection;
            ViewModel.CanApprovePullRequest = hasSelection;
            ViewModel.CanRequestPullRequestChanges = hasSelection;
            ViewModel.IsMergeEnabled = hasSelection;
            ViewModel.CanMergeWithMergeCommit = hasSelection;
            ViewModel.CanMergeWithSquash = hasSelection;
            ViewModel.CanMergeWithRebase = hasSelection;
            ViewModel.ArePullRequestActionsEnabled = hasSelection;
        }
        ProductPerformanceReadiness.CommitRoute(
            "repo_pull_requests",
            $"{ProductPerformanceReadiness.CountIdentity(ViewModel.PullRequests.Count)};selected={ViewModel.SelectedPullRequest?.Id ?? 0}");
        if (isReplyIdentityAutomationScenario)
        {
            PullRequestSectionSegmented.SelectedIndex = 3;
            PullRequestSectionComboBox.SelectedIndex = 3;
            ViewModel.SetSection(PullRequestWorkspaceSection.Reviews);
        }

        _initialized = true;
        UpdatePaneButtonVisibility();
        MaybeOpenInitialPullRequestListDrawer();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _selectionRenderGeneration++;
        _pendingPointerHydrationNumber = null;
        ViewModel.CancelPredictivePrefetches();
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = null;
        base.OnNavigatedFrom(e);
    }

    private void PullRequestContentScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            AttachPerformanceScrollProbe);
    }

    private void AttachPerformanceScrollProbe()
    {
        _performanceScrollProbe?.Dispose();
        _performanceScrollProbe = FindDescendant<ScrollViewer>(PullRequestContentScrollViewer) is ScrollViewer scrollViewer
            ? ProductPerformanceScrollProbe.TryStart(PullRequestContentHost, scrollViewer)
            : null;
    }

    private void PullRequestContentScrollViewer_Unloaded(object sender, RoutedEventArgs e)
    {
        _performanceScrollProbe?.Dispose();
        _performanceScrollProbe = null;
    }

    private void PullRequestDetailHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_initialized)
        {
            UpdatePaneButtonVisibility();
        }
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is T descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private async void PullRequestStateSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        int selectedIndex = Math.Clamp(PullRequestStateSegmented.SelectedIndex, 0, ViewModel.StateOptions.Count - 1);
        ViewModel.SelectedStateOption = ViewModel.StateOptions[selectedIndex];
        await ViewModel.ApplyFiltersAsync();
    }

    private async void PullRequestFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        await ViewModel.ApplyFiltersAsync();
    }

    private async void PullRequestSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        ViewModel.SearchText = PullRequestSearchBox.Text;
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        CancellationTokenSource debounce = new();
        _searchDebounce = debounce;
        try
        {
            await Task.Delay(220, debounce.Token);
            await ViewModel.ApplyFiltersAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PullRequestsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GitHubPullRequest pullRequest)
        {
            if (PullRequestsWorkspace.IsLeadingDrawerOpen)
            {
                ListViewScrollAnchor anchor = ListViewScrollAnchor.Capture(PullRequestsList);
                PullRequestsWorkspace.CloseDrawer();
                anchor.RestoreAcrossLayoutPasses(DispatcherQueue);
            }

            if (_pendingPointerHydrationNumber == pullRequest.Number)
            {
                return;
            }

            ViewModel.SelectedPullRequest = pullRequest;
        }
    }

    private void PullRequestsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView { SelectedItem: GitHubPullRequest pullRequest })
        {
            return;
        }

        if (_pointerSelectionInProgress)
        {
            return;
        }

        int generation = BeginPullRequestTraversal(pullRequest);
        PrimePullRequestSelection(pullRequest);
        SchedulePullRequestSelection(pullRequest, generation);
        if (ProductPerformanceReadiness.IsEnabled)
        {
            SchedulePullRequestTraversalCommit(pullRequest, generation);
        }
    }

    private void PullRequestListItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        GitHubPullRequest? pullRequest = sender switch
        {
            ListViewItem { Content: GitHubPullRequest item } => item,
            FrameworkElement { DataContext: GitHubPullRequest item } => item,
            _ => null
        };
        if (pullRequest is null ||
            sender is not UIElement pointerRoot ||
            e.GetCurrentPoint(pointerRoot).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        int generation = BeginPullRequestTraversal(pullRequest);
        ProductPerformanceReadiness.RecordTraversalStage("repo_pull_requests.pointer.selected");
        PrimePullRequestSelection(pullRequest);
        _pendingPointerHydrationNumber = pullRequest.Number;
        _pointerSelectionInProgress = true;
        try
        {
            PullRequestsList.SelectedItem = pullRequest;
        }
        finally
        {
            _pointerSelectionInProgress = false;
        }

        e.Handled = true;
        SchedulePullRequestSelection(pullRequest, generation, focusSelection: true);
        if (ProductPerformanceReadiness.IsEnabled)
        {
            SchedulePullRequestTraversalCommit(pullRequest, generation);
        }
        if (PullRequestsWorkspace.IsLeadingDrawerOpen)
        {
            ListViewScrollAnchor anchor = ListViewScrollAnchor.Capture(PullRequestsList);
            PullRequestsWorkspace.CloseDrawer();
            anchor.RestoreAcrossLayoutPasses(DispatcherQueue);
        }
    }

    private void PrimePullRequestSelection(GitHubPullRequest pullRequest)
    {
        if (!string.Equals(PullRequestDetailTitle.Text, pullRequest.Title, StringComparison.Ordinal))
        {
            PullRequestDetailTitle.Text = pullRequest.Title;
        }
    }

    private int BeginPullRequestTraversal(GitHubPullRequest pullRequest)
    {
        int generation = ++_selectionRenderGeneration;
        if (!ProductPerformanceReadiness.IsEnabled)
        {
            return generation;
        }

        ProductPerformanceReadiness.BeginTraversal(
            "repo_pull_requests",
            pullRequest.AutomationId,
            "repo_pull_requests");
        return generation;
    }

    private void SchedulePullRequestTraversalCommit(
        GitHubPullRequest pullRequest,
        int generation)
    {
        ProductPerformanceRenderCommitter.ScheduleAfterNextFrame(
            this,
            () => generation == _selectionRenderGeneration &&
                IsLoaded &&
                PullRequestsList.SelectedItem is GitHubPullRequest selected &&
                selected.Number == pullRequest.Number,
            () =>
                string.Equals(
                    PullRequestDetailTitle.Text,
                    pullRequest.Title,
                    StringComparison.Ordinal),
            () => ProductPerformanceReadiness.CommitTraversal(
                "repo_pull_requests",
                pullRequest.AutomationId));
    }

    private void SchedulePullRequestSelection(
        GitHubPullRequest pullRequest,
        int generation,
        bool focusSelection = false)
    {
        DeferredFrameAction.Schedule(
            this,
            () => generation == _selectionRenderGeneration &&
                IsLoaded &&
                PullRequestsList.SelectedItem is GitHubPullRequest current &&
                current.Number == pullRequest.Number,
            () =>
            {
                _pendingPointerHydrationNumber = null;
                ViewModel.SelectedPullRequest = pullRequest;
                if (focusSelection &&
                    PullRequestsList.ContainerFromItem(pullRequest) is Control container)
                {
                    container.Focus(FocusState.Pointer);
                }
            });
    }

    private void PullRequestsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container)
        {
            return;
        }

        container.GotFocus -= PullRequestListItemContainer_GotFocus;
        container.RemoveHandler(
            PointerPressedEvent,
            new PointerEventHandler(PullRequestListItem_PointerPressed));
        if (args.InRecycleQueue)
        {
            return;
        }

        container.GotFocus += PullRequestListItemContainer_GotFocus;
        container.AddHandler(
            PointerPressedEvent,
            new PointerEventHandler(PullRequestListItem_PointerPressed),
            handledEventsToo: true);
        if (args.Item is GitHubPullRequest pullRequest)
        {
            AutomationProperties.SetAutomationId(container, pullRequest.AutomationId);
            AutomationProperties.SetName(container, pullRequest.AutomationName);
        }
    }

    private void PullRequestDetailList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            AutomationProperties.SetAutomationId(container, string.Empty);
            AutomationProperties.SetName(container, string.Empty);
            return;
        }

        (string? automationId, string? automationName) = args.Item switch
        {
            GitHubIssueComment comment =>
                (comment.MarkdownAutomationId, $"Pull request comment by {comment.AuthorDisplayName}"),
            GitHubCommit commit => (commit.AutomationId, commit.AutomationName),
            RepoPullRequestPageViewModel.PullRequestReviewItem review =>
                (review.AutomationId, $"Review by {review.ReviewerLogin}: {review.StateText}"),
            GitHubIssueEvent timelineEvent =>
                (timelineEvent.ActorAutomationId, $"{timelineEvent.Summary}. {timelineEvent.MetaText}"),
            _ => (null, null)
        };

        AutomationProperties.SetAutomationId(container, automationId ?? string.Empty);
        AutomationProperties.SetName(container, automationName ?? string.Empty);
    }

    private void PullRequestListItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GitHubPullRequest pullRequest })
        {
            ViewModel.PrefetchPullRequest(pullRequest, PullRequestPrefetchReason.Hover);
        }
    }

    private void PullRequestListItemContainer_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is ListViewItem { Content: GitHubPullRequest pullRequest })
        {
            ViewModel.PrefetchPullRequest(pullRequest, PullRequestPrefetchReason.Hover);
        }
    }

    private void PullRequestsWorkspace_ModeChanged(object? sender, AdaptiveWorkspaceState e)
    {
        UpdatePaneButtonVisibility();
        MaybeOpenInitialPullRequestListDrawer();
    }

    public void OpenPullRequestListPane()
        => PullRequestsWorkspace.OpenLeadingPane();

    public void OpenPullRequestInspectorPane()
        => PullRequestsWorkspace.OpenTrailingPane();

    private void OpenListPaneButton_Click(object sender, RoutedEventArgs e)
        => OpenPullRequestListPane();

    private void OpenInspectorPaneButton_Click(object sender, RoutedEventArgs e)
        => OpenPullRequestInspectorPane();

    private void CloseWorkspaceDrawerButton_Click(object sender, RoutedEventArgs e)
        => PullRequestsWorkspace.CloseDrawer();

    private void UpdatePaneButtonVisibility()
    {
        AdaptiveWorkspaceState? state = PullRequestsWorkspace.State;
        bool isLeadingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Leading;
        bool isTrailingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Trailing;
        RepoPullRequestsOpenListPaneButton.Visibility = state?.ShouldShowLeadingPaneButton == true && !isLeadingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoPullRequestsCloseListPaneButton.Visibility = isLeadingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoPullRequestsOpenInspectorPaneButton.Visibility = state?.ShouldShowTrailingPaneButton == true && !isTrailingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoPullRequestsCloseInspectorPaneButton.Visibility = isTrailingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;

        bool isCompact = state?.Mode is AdaptiveWorkspaceMode.Narrow or AdaptiveWorkspaceMode.Compact;
        PullRequestDetailHeader.Padding = isCompact
            ? new Thickness(8, 6, 10, 8)
            : new Thickness(10, 10, 16, 12);
        PullRequestDetailTitle.MaxLines = isCompact ? 1 : 2;
        PullRequestDetailTitle.TextWrapping = isCompact
            ? TextWrapping.NoWrap
            : TextWrapping.WrapWholeWords;
        PullRequestSectionSegmented.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        PullRequestSectionComboBox.Visibility = isCompact ? Visibility.Visible : Visibility.Collapsed;
        bool useCompactActionOverflow = state is not null && state.Mode != AdaptiveWorkspaceMode.Wide;
        RepoPullRequestsInlineActions.Visibility = useCompactActionOverflow
            ? Visibility.Collapsed
            : Visibility.Visible;
        RepoPullRequestsCompactActionsButton.Visibility = useCompactActionOverflow
            ? Visibility.Visible
            : Visibility.Collapsed;
        PullRequestCommentForm.EditorHeight = 130;
        double availableDetailHeight = PullRequestDetailHost.ActualHeight > 0
            ? PullRequestDetailHost.ActualHeight
            : ActualHeight;
        const double DetailChromeAndMinimumConversationHeight = 390;
        bool useCompactComposer = isCompact ||
            availableDetailHeight < PullRequestCommentForm.EffectiveEditorHeight +
            DetailChromeAndMinimumConversationHeight;
        PullRequestCommentForm.Visibility = useCompactComposer ? Visibility.Collapsed : Visibility.Visible;
        RepoPullRequestsOpenCompactCommentButton.Visibility = useCompactComposer ? Visibility.Visible : Visibility.Collapsed;
        PullRequestContentScrollViewer.Padding = isCompact
            ? new Thickness(12, 10, 12, 12)
            : new Thickness(18);
        PullRequestCommentFormHost.Padding = isCompact
            ? new Thickness(12, 8, 12, 8)
            : new Thickness(18, 12, 18, 18);
    }

    private void MaybeOpenInitialPullRequestListDrawer()
    {
        if (_openedInitialListDrawer ||
            !_initialized ||
            ViewModel.HasSelectedPullRequest ||
            PullRequestsWorkspace.State is not { ShouldShowLeadingPaneButton: true })
        {
            return;
        }

        _openedInitialListDrawer = true;
        PullRequestsWorkspace.OpenLeadingPane();
    }

    private void PullRequestSectionSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        int selectedIndex = PullRequestSectionSegmented.SelectedIndex;
        if (PullRequestSectionComboBox.SelectedIndex != selectedIndex)
        {
            PullRequestSectionComboBox.SelectedIndex = selectedIndex;
        }

        ViewModel.SetSection(PullRequestSectionSelectionPolicy.FromIndex(selectedIndex));
    }

    private void PullRequestSectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        int selectedIndex = PullRequestSectionComboBox.SelectedIndex;
        if (PullRequestSectionSegmented.SelectedIndex != selectedIndex)
        {
            PullRequestSectionSegmented.SelectedIndex = selectedIndex;
        }

        ViewModel.SetSection(PullRequestSectionSelectionPolicy.FromIndex(selectedIndex));
    }

    private async void TogglePullRequestStateButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ToggleSelectedPullRequestStateAsync();
    }

    private async void CommentButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.AddPullRequestCommentAsync();
    }

    private void CompactCommentFlyout_Closed(object sender, object e)
    {
        RepoPullRequestsOpenCompactCommentButton.Focus(FocusState.Programmatic);
    }

    private async void SubmitReviewButton_Click(object sender, RoutedEventArgs e)
    {
        RadioButton commentOption = CreateReviewDecisionOption(
            "RepoPullRequestsReviewDecisionComment",
            L("RepoPullRequests/Dialogs/Review/DecisionComment", "Comment"),
            ViewModel.CanSubmitReviewComment);
        RadioButton approveOption = CreateReviewDecisionOption(
            "RepoPullRequestsReviewDecisionApprove",
            L("RepoPullRequests/Dialogs/Review/DecisionApprove", "Approve"),
            ViewModel.CanApprovePullRequest);
        RadioButton requestChangesOption = CreateReviewDecisionOption(
            "RepoPullRequestsReviewDecisionRequestChanges",
            L("RepoPullRequests/Dialogs/Review/DecisionRequestChanges", "Request changes"),
            ViewModel.CanRequestPullRequestChanges);
        RadioButton? initialOption = new[] { commentOption, approveOption, requestChangesOption }
            .FirstOrDefault(option => option.IsEnabled);
        if (initialOption is null)
        {
            return;
        }

        initialOption.IsChecked = true;
        MarkdownForm reviewForm = new()
        {
            EditorHeight = 180,
            DocumentSource = ViewModel.PullRequestCommentMarkdownSource,
            Text = string.Empty
        };
        AutomationProperties.SetAutomationId(reviewForm, "RepoPullRequestsReviewBody");
        AutomationProperties.SetName(reviewForm, L("RepoPullRequests/Dialogs/Review/BodyAutomationName", "Pull request review body"));

        TextBlock validationText = AppContentDialogPresenter.CreateInlineErrorPresenter(
            "RepoPullRequestsReviewValidationText");
        AutomationProperties.SetAutomationId(validationText, "RepoPullRequestsReviewValidationText");
        AutomationProperties.SetName(validationText, L("RepoPullRequests/Dialogs/Review/ValidationAutomationName", "Review validation message"));

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = L("RepoPullRequests/Dialogs/Review/Title", "Submit review"),
            Content = new StackPanel
            {
                MaxWidth = 520,
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = L("RepoPullRequests/Dialogs/Review/DecisionHeader", "Review decision"),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    },
                    commentOption,
                    approveOption,
                    requestChangesOption,
                    reviewForm,
                    validationText
                }
            },
            PrimaryButtonText = L("RepoPullRequests/Dialogs/Review/Primary", "Submit review"),
            CloseButtonText = L("Common/Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoPullRequestsSubmitReviewDialog");
        AutomationProperties.SetName(dialog, L("RepoPullRequests/Dialogs/Review/AutomationName", "Submit pull request review"));
        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                PullRequestReviewSubmission submission = CreateReviewSubmission(
                    commentOption,
                    approveOption,
                    reviewForm.Text);
                try
                {
                    PullRequestReviewSubmissionPolicy.Validate(submission);
                }
                catch (ArgumentException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Pull request review validation failed: {ex}");
                    return DialogMutationResult.Failure(L(
                        "RepoPullRequests/Dialogs/Review/CommentRequired",
                        "Enter a review comment before commenting or requesting changes."));
                }

                string previousStatus = ViewModel.StatusText;
                await ViewModel.SubmitPullRequestReviewAsync(submission.Decision, submission.Body);
                return ResolvePullRequestMutationResult(
                    previousStatus,
                    ViewModel.SelectedSection == PullRequestWorkspaceSection.Reviews,
                    "submitted");
            },
            validationText);
    }

    private static RadioButton CreateReviewDecisionOption(
        string automationId,
        string label,
        bool isEnabled)
    {
        RadioButton option = new()
        {
            Content = label,
            GroupName = "PullRequestReviewDecision",
            IsEnabled = isEnabled
        };
        AutomationProperties.SetAutomationId(option, automationId);
        AutomationProperties.SetName(option, label);
        return option;
    }

    private static PullRequestReviewSubmission CreateReviewSubmission(
        RadioButton commentOption,
        RadioButton approveOption,
        string body)
    {
        PullRequestReviewDecision decision = approveOption.IsChecked == true
            ? PullRequestReviewDecision.Approve
            : commentOption.IsChecked == true
                ? PullRequestReviewDecision.Comment
                : PullRequestReviewDecision.RequestChanges;
        return new PullRequestReviewSubmission(decision, body);
    }

    private async void ReviewReplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RepoPullRequestPageViewModel.PullRequestReviewThreadItem thread })
        {
            await ViewModel.ReplyToReviewCommentAsync(thread);
        }
    }

    private async void NewPullRequestButton_Click(object sender, RoutedEventArgs e)
    {
        RepoPullRequestPageViewModel.PullRequestCreateDialogData? data = await ViewModel.LoadCreateDialogDataAsync();
        if (data is null)
        {
            return;
        }

        TextBox titleBox = new()
        {
            Header = ViewModel.TitleHeaderText,
            PlaceholderText = L("RepoPullRequests/Dialogs/TitlePlaceholder", "Pull request title"),
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(titleBox, "RepoPullRequestsCreateTitleBox");
        AutomationProperties.SetName(titleBox, L("RepoPullRequests/Dialogs/TitleAutomationName", "Pull request title"));
        TextBox headBox = new()
        {
            Header = ViewModel.HeadBranchHeaderText,
            PlaceholderText = ViewModel.HeadBranchDialogPlaceholderText,
            Text = data.DefaultHead
        };
        AutomationProperties.SetAutomationId(headBox, "RepoPullRequestsCreateHeadBranchBox");
        AutomationProperties.SetName(headBox, L("RepoPullRequests/Dialogs/Create/HeadAutomationName", "Pull request head branch"));
        TextBox baseBox = new()
        {
            Header = ViewModel.BaseBranchHeaderText,
            Text = data.DefaultBase
        };
        AutomationProperties.SetAutomationId(baseBox, "RepoPullRequestsCreateBaseBranchBox");
        AutomationProperties.SetName(baseBox, L("RepoPullRequests/Dialogs/Create/BaseAutomationName", "Pull request base branch"));
        TextBox bodyBox = new()
        {
            Header = ViewModel.DescriptionHeaderText,
            PlaceholderText = L("RepoPullRequests/Dialogs/DescriptionPlaceholder", "Add a description..."),
            AcceptsReturn = true,
            Height = 180,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(bodyBox, "RepoPullRequestsCreateBodyBox");
        AutomationProperties.SetName(bodyBox, L("RepoPullRequests/Dialogs/DescriptionAutomationName", "Pull request description"));
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter(
            "RepoPullRequestsCreateDialogError");
        StackPanel content = new()
        {
            Spacing = 12,
            Children =
            {
                errorText,
                titleBox,
                headBox,
                baseBox,
                bodyBox
            }
        };
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.NewPullRequestDialogTitle,
            Content = content,
            PrimaryButtonText = ViewModel.CreateButtonText,
            CloseButtonText = ViewModel.CancelButtonText,
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoPullRequestsCreateDialog");
        AutomationProperties.SetName(dialog, L("RepoPullRequests/Dialogs/Create/AutomationName", "Create pull request"));

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                if (string.IsNullOrWhiteSpace(titleBox.Text) ||
                    string.IsNullOrWhiteSpace(headBox.Text) ||
                    string.IsNullOrWhiteSpace(baseBox.Text))
                {
                    return DialogMutationResult.Failure(
                        L("RepoPullRequests/Dialogs/Create/RequiredFields", "Title, head branch, and base branch are required."));
                }

                string previousStatus = ViewModel.StatusText;
                await ViewModel.CreatePullRequestAsync(
                    titleBox.Text.Trim(),
                    headBox.Text.Trim(),
                    baseBox.Text.Trim(),
                    bodyBox.Text);
                return ResolvePullRequestMutationResult(
                    previousStatus,
                    string.Equals(ViewModel.SelectedPullRequest?.Title, titleBox.Text.Trim(), StringComparison.Ordinal),
                    "created pull request");
            },
            errorText);
    }

    private async void EditPullRequestButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedPullRequest is null)
        {
            return;
        }

        TextBox titleBox = new()
        {
            Header = ViewModel.TitleHeaderText,
            Text = ViewModel.SelectedPullRequest.Title,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(titleBox, "RepoPullRequestsEditTitleBox");
        AutomationProperties.SetName(titleBox, L("RepoPullRequests/Dialogs/TitleAutomationName", "Pull request title"));
        TextBox bodyBox = new()
        {
            Header = ViewModel.DescriptionHeaderText,
            Text = ViewModel.PullRequestBodyText,
            AcceptsReturn = true,
            Height = 220,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(bodyBox, "RepoPullRequestsEditBodyBox");
        AutomationProperties.SetName(bodyBox, L("RepoPullRequests/Dialogs/DescriptionAutomationName", "Pull request description"));
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter(
            "RepoPullRequestsEditDialogError");
        StackPanel content = new()
        {
            Spacing = 12,
            Children =
            {
                titleBox,
                bodyBox,
                errorText
            }
        };
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.FormatEditPullRequestDialogTitle(ViewModel.SelectedPullRequest.Number),
            Content = content,
            PrimaryButtonText = ViewModel.SaveButtonText,
            CloseButtonText = ViewModel.CancelButtonText,
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoPullRequestsEditDialog");
        AutomationProperties.SetName(dialog, L("RepoPullRequests/Dialogs/Edit/AutomationName", "Edit pull request"));

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                if (string.IsNullOrWhiteSpace(titleBox.Text))
                {
                    titleBox.Focus(FocusState.Programmatic);
                    return DialogMutationResult.Failure(L("RepoPullRequests/Dialogs/Edit/TitleRequired", "Pull request title is required."));
                }

                string previousStatus = ViewModel.StatusText;
                await ViewModel.UpdateSelectedPullRequestAsync(titleBox.Text.Trim(), bodyBox.Text);
                return ResolvePullRequestMutationResult(
                    previousStatus,
                    string.Equals(ViewModel.SelectedPullRequest?.Title, titleBox.Text.Trim(), StringComparison.Ordinal),
                    "updated");
            },
            errorText);
    }

    private async void MetadataButton_Click(object sender, RoutedEventArgs e)
    {
        RepoPullRequestPageViewModel.PullRequestMetadataDialogData? data = await ViewModel.LoadSelectedPullRequestMetadataDialogDataAsync();
        if (data is null || ViewModel.SelectedPullRequest is null)
        {
            return;
        }

        TextBox reviewersBox = new()
        {
            Header = ViewModel.RequestedReviewersSectionTitle,
            Text = string.Join(", ", ViewModel.RequestedReviewers.Select(reviewer => reviewer.Login)),
            PlaceholderText = L("RepoPullRequests/Dialogs/Metadata/UsersPlaceholder", "user1, user2")
        };
        AutomationProperties.SetAutomationId(reviewersBox, "RepoPullRequestsMetadataReviewersBox");
        AutomationProperties.SetName(reviewersBox, L("RepoPullRequests/Dialogs/Metadata/ReviewersAutomationName", "Requested reviewers"));
        TextBox assigneesBox = new()
        {
            Header = ViewModel.AssigneesSectionTitle,
            Text = string.Join(", ", ViewModel.SelectedAssignees.Select(assignee => assignee.Login)),
            PlaceholderText = L("RepoPullRequests/Dialogs/Metadata/UsersPlaceholder", "user1, user2")
        };
        AutomationProperties.SetAutomationId(assigneesBox, "RepoPullRequestsMetadataAssigneesBox");
        AutomationProperties.SetName(assigneesBox, L("RepoPullRequests/Dialogs/Metadata/AssigneesAutomationName", "Pull request assignees"));
        TextBox labelsBox = new()
        {
            Header = ViewModel.LabelsSectionTitle,
            Text = string.Join(", ", ViewModel.SelectedLabels.Select(label => label.Name)),
            PlaceholderText = L("RepoPullRequests/Dialogs/Metadata/LabelsPlaceholder", "bug, ui")
        };
        AutomationProperties.SetAutomationId(labelsBox, "RepoPullRequestsMetadataLabelsBox");
        AutomationProperties.SetName(labelsBox, L("RepoPullRequests/Dialogs/Metadata/LabelsAutomationName", "Pull request labels"));
        ComboBox milestoneBox = new()
        {
            Header = ViewModel.MilestoneHeaderText,
            DisplayMemberPath = nameof(GitHubMilestone.Title),
            ItemsSource = data.AvailableMilestones,
            SelectedItem = data.AvailableMilestones.FirstOrDefault(milestone => milestone.Title == ViewModel.MilestoneTitle)
        };
        AutomationProperties.SetAutomationId(milestoneBox, "RepoPullRequestsMetadataMilestonePicker");
        AutomationProperties.SetName(milestoneBox, L("RepoPullRequests/Dialogs/Metadata/MilestoneAutomationName", "Pull request milestone"));
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter(
            "RepoPullRequestsMetadataDialogError");
        StackPanel content = new()
        {
            Spacing = 12,
            Children =
            {
                reviewersBox,
                assigneesBox,
                labelsBox,
                milestoneBox,
                errorText
            }
        };
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.FormatMetadataDialogTitle(ViewModel.SelectedPullRequest.Number),
            Content = content,
            PrimaryButtonText = ViewModel.SaveButtonText,
            CloseButtonText = ViewModel.CancelButtonText,
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoPullRequestsMetadataDialog");
        AutomationProperties.SetName(dialog, L("RepoPullRequests/Dialogs/Metadata/AutomationName", "Edit pull request metadata"));

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                GitHubMilestone? milestone = milestoneBox.SelectedItem as GitHubMilestone;
                string previousStatus = ViewModel.StatusText;
                await ViewModel.UpdateSelectedPullRequestMetadataAsync(new RepoPullRequestPageViewModel.PullRequestMetadataUpdate(
                    SplitCsv(reviewersBox.Text),
                    SplitCsv(assigneesBox.Text),
                    SplitCsv(labelsBox.Text),
                    milestone?.Number));
                return ResolvePullRequestMutationResult(previousStatus, false, "updated");
            },
            errorText);
    }

    private async void PullRequestReactionsButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<GitHubReaction>? reactions = await ViewModel.GetSelectedPullRequestReactionsAsync();
        if (reactions is null || ViewModel.SelectedPullRequest is null)
        {
            return;
        }

        string viewerLogin = ViewModel.AuthenticatedLogin;
        Dictionary<string, long> viewerReactionIds = reactions
            .Where(reaction => string.Equals(reaction.User.Login, viewerLogin, StringComparison.OrdinalIgnoreCase))
            .GroupBy(static reaction => reaction.Content, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> counts = reactions
            .GroupBy(static reaction => reaction.Content, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);

        StackPanel options = new() { Spacing = 6 };
        foreach (string content in SupportedReactionContents)
        {
            CheckBox option = new()
            {
                Content = GitHubReactionTextFormatter.FormatPickerLabel(content, counts.GetValueOrDefault(content)),
                IsChecked = viewerReactionIds.ContainsKey(content),
                Tag = content
            };
            AutomationProperties.SetAutomationId(option, $"RepoPullRequestsReaction_{ToAutomationToken(content)}");
            AutomationProperties.SetName(
                option,
                LF("RepoPullRequests/Dialogs/Reactions/ToggleAutomationNameFormat", "Toggle {0} reaction", content));
            options.Children.Add(option);
        }

        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter(
            "RepoPullRequestsReactionDialogError");
        options.Children.Add(errorText);
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.SelectedPullRequestReactionDialogTitle,
            Content = options,
            PrimaryButtonText = ViewModel.ReactionDialogSaveButtonText,
            CloseButtonText = ViewModel.CancelButtonText,
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoPullRequestsReactionDialog");
        AutomationProperties.SetName(dialog, L("RepoPullRequests/Dialogs/Reactions/AutomationName", "Manage pull request reactions"));

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                HashSet<string> selected = options.Children
                    .OfType<CheckBox>()
                    .Where(static option => option.IsChecked == true)
                    .Select(static option => (string)option.Tag)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                bool noChanges = selected.SetEquals(viewerReactionIds.Keys);
                string previousStatus = ViewModel.StatusText;
                string previousSummary = ViewModel.PullRequestReactionsText;
                await ViewModel.ApplySelectedPullRequestReactionSelectionAsync(selected, viewerReactionIds);
                return ResolvePullRequestMutationResult(
                    previousStatus,
                    noChanges || !string.Equals(
                        previousSummary,
                        ViewModel.PullRequestReactionsText,
                        StringComparison.Ordinal),
                    "reaction updated");
            },
            errorText);
    }

    private void PreviousDiffMatchButton_Click(object sender, RoutedEventArgs e)
        => ViewModel.MovePullRequestDiffSearchMatch(-1);

    private void NextDiffMatchButton_Click(object sender, RoutedEventArgs e)
        => ViewModel.MovePullRequestDiffSearchMatch(1);

    private async void MergeCommitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowMergeDialogAsync("merge");
    }

    private async void SquashMergeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowMergeDialogAsync("squash");
    }

    private async void RebaseMergeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowMergeDialogAsync("rebase");
    }

    private async Task ShowMergeDialogAsync(string mergeMethod)
    {
        string operationTitle = ViewModel.FormatMergeOperationTitle(mergeMethod);
        TextBox titleBox = new()
        {
            Header = ViewModel.CommitTitleHeaderText,
            PlaceholderText = L("RepoPullRequests/Dialogs/Merge/TitlePlaceholder", "Optional merge commit title"),
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(titleBox, "RepoPullRequestsMergeTitleBox");
        AutomationProperties.SetName(titleBox, L("RepoPullRequests/Dialogs/Merge/TitleAutomationName", "Merge commit title"));
        TextBox messageBox = new()
        {
            Header = ViewModel.CommitMessageHeaderText,
            PlaceholderText = L("RepoPullRequests/Dialogs/Merge/MessagePlaceholder", "Optional merge commit message"),
            AcceptsReturn = true,
            Height = 160,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(messageBox, "RepoPullRequestsMergeMessageBox");
        AutomationProperties.SetName(messageBox, L("RepoPullRequests/Dialogs/Merge/MessageAutomationName", "Merge commit message"));
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter(
            "RepoPullRequestsMergeDialogError");
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = operationTitle,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    titleBox,
                    messageBox,
                    errorText
                }
            },
            PrimaryButtonText = ViewModel.MergeButtonText,
            CloseButtonText = ViewModel.CancelButtonText,
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoPullRequestsMergeDialog");
        AutomationProperties.SetName(
            dialog,
            LF("RepoPullRequests/Dialogs/Merge/AutomationNameFormat", "{0} pull request", operationTitle));

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                string previousStatus = ViewModel.StatusText;
                await ViewModel.MergeSelectedPullRequestAsync(
                    mergeMethod,
                    operationTitle,
                    string.IsNullOrWhiteSpace(titleBox.Text) ? null : titleBox.Text,
                    string.IsNullOrWhiteSpace(messageBox.Text) ? null : messageBox.Text);
                return ResolvePullRequestMutationResult(previousStatus, false, "merged");
            },
            errorText);
    }

    private static string L(string key, string fallback) =>
        LocalizedResourceText.GetString(key, fallback);

    private static string LF(string key, string fallback, params object?[] arguments) =>
        LocalizedResourceText.Format(key, fallback, arguments);

    private static string[] SplitCsv(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly string[] SupportedReactionContents =
        ["+1", "-1", "laugh", "hooray", "confused", "heart", "rocket", "eyes"];

    private static string ToAutomationToken(string content) => content switch
    {
        "+1" => "PlusOne",
        "-1" => "MinusOne",
        _ => char.ToUpperInvariant(content[0]) + content[1..]
    };

    private DialogMutationResult ResolvePullRequestMutationResult(
        string previousStatus,
        bool observableSuccess,
        string successText)
    {
        string currentStatus = ViewModel.StatusText ?? string.Empty;
        DialogMutationOutcome outcome = DialogMutationOutcomePolicy.Resolve(
            previousStatus,
            currentStatus,
            observableSuccess,
            successText,
            "JitHub could not complete this pull request action.");
        return outcome.Succeeded
            ? DialogMutationResult.Success()
            : DialogMutationResult.Failure(outcome.ErrorMessage);
    }
}
