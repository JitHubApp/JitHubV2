using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services.Layout;
using JitHub.Services;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.Performance;
using JitHub.WinUI.Views.Controls.Common;
using JitHub.WinUI.Views.Controls.Issue;
using JitHub.WinUI.Views.Dialogs;
using JitHub.WinUI.ViewModels.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class RepoIssuePage : Page
{
    private bool _initialized;
    private bool _openedInitialListDrawer;
    private int _workspaceChromeRealizationVersion;
    private RepoIssueListPane? _issueListPane;
    private RepoIssueInspectorPane? _issueInspectorPane;
    private RepoIssueDetailPane? _issueDetailPane;
    private long _issueTraversalGeneration;

    public RepoIssuePageViewModel ViewModel { get; }

    public RepoIssuePage()
    {
        NavigationCacheMode = NavigationCacheMode.Required;
        ViewModel = ((App)Application.Current).GetService<RepoIssuePageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        Unloaded += RepoIssuePage_Unloaded;
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RepoIssuePageViewModel.IsIssueContentVisible) &&
                ViewModel.IsIssueContentVisible)
            {
                ScheduleIssueDetailPaneRealization();
            }
        };
    }

    private void RepoIssuePage_Unloaded(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _issueTraversalGeneration);
        ViewModel.CancelNavigationWork();
        _issueListPane?.CancelPendingWork();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _openedInitialListDrawer = false;
        IssueNavArg? arg = e.Parameter as IssueNavArg;
        Task initialization = ViewModel.InitializeForNavigationAsync(arg);
        bool committedCachedDetail = ViewModel.SelectedIssue is not null;
        if (committedCachedDetail)
        {
            CommitPerformanceReadiness();
        }

        await initialization;
        if (DialogMatrixAutomationScenario.IsEnabled)
        {
            ViewModel.CanCreateIssue = true;
            ViewModel.CanEditIssue = ViewModel.SelectedIssue is not null;
            ViewModel.CanManageIssueMetadata = ViewModel.SelectedIssue is not null;
            ViewModel.CanReactToIssue = ViewModel.SelectedIssue is not null;
            ViewModel.AreIssueActionsEnabled = ViewModel.SelectedIssue is not null;
        }
        if (!committedCachedDetail)
        {
            CommitPerformanceReadiness();
        }

        _initialized = true;
        UpdatePaneButtonVisibility();
        MaybeOpenInitialIssueListDrawer();
        ScheduleWorkspaceChromeRealization(arg?.IsNotificationHandoff == true);
        ScheduleIssueDetailPaneRealization();
    }

    private void CommitPerformanceReadiness() =>
        ProductPerformanceReadiness.CommitRoute(
            "repo_issues",
            $"{ProductPerformanceReadiness.CountIdentity(ViewModel.Issues.Count)};selected={ViewModel.SelectedIssue?.Id ?? 0}");

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Interlocked.Increment(ref _issueTraversalGeneration);
        ViewModel.CancelNavigationWork();
        _issueListPane?.CancelPendingWork();
        _workspaceChromeRealizationVersion++;
        base.OnNavigatedFrom(e);
    }

    private void IssuesWorkspace_ModeChanged(object? sender, AdaptiveWorkspaceState e)
    {
        UpdatePaneButtonVisibility();
        MaybeOpenInitialIssueListDrawer();
    }

    public void OpenIssueListPane()
    {
        EnsureIssueListPane();
        IssuesWorkspace.OpenLeadingPane();
    }

    public void OpenIssueInspectorPane()
    {
        EnsureIssueInspectorPane();
        IssuesWorkspace.OpenTrailingPane();
    }

    private void OpenListPaneButton_Click(object sender, RoutedEventArgs e)
        => OpenIssueListPane();

    private void OpenInspectorPaneButton_Click(object sender, RoutedEventArgs e)
        => OpenIssueInspectorPane();

    private void CloseWorkspaceDrawerButton_Click(object sender, RoutedEventArgs e)
        => IssuesWorkspace.CloseDrawer();

    private void UpdatePaneButtonVisibility()
    {
        AdaptiveWorkspaceState? state = IssuesWorkspace.State;
        bool isLeadingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Leading;
        bool isTrailingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Trailing;
        _issueListPane?.SetDrawerOpen(isLeadingDrawerOpen);
        _issueInspectorPane?.UpdateResponsiveState(
            isTrailingDrawerOpen,
            state?.Mode is AdaptiveWorkspaceMode.Narrow or AdaptiveWorkspaceMode.Compact);
        _issueDetailPane?.UpdateResponsiveState(state);
    }

    private void MaybeOpenInitialIssueListDrawer()
    {
        if (_openedInitialListDrawer ||
            !_initialized ||
            ViewModel.HasSelectedIssue ||
            IssuesWorkspace.State is not { ShouldShowLeadingPaneButton: true })
        {
            return;
        }

        _openedInitialListDrawer = true;
        EnsureIssueListPane();
        IssuesWorkspace.OpenLeadingPane();
    }

    private void ScheduleWorkspaceChromeRealization(bool deferForNotificationHandoff)
    {
        int version = ++_workspaceChromeRealizationVersion;
        if (deferForNotificationHandoff)
        {
            _ = ScheduleDeferredWorkspaceChromeRealizationAsync(version);
            return;
        }

        EnqueueWorkspaceChromeRealization(version);
    }

    private async Task ScheduleDeferredWorkspaceChromeRealizationAsync(int version)
    {
        await Task.Delay(500);
        if (version == _workspaceChromeRealizationVersion)
        {
            EnqueueWorkspaceChromeRealization(version);
        }
    }

    private void EnqueueWorkspaceChromeRealization(int version)
    {
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (version != _workspaceChromeRealizationVersion)
            {
                return;
            }

            EnsureIssueListPane();
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (version == _workspaceChromeRealizationVersion)
                {
                    EnsureIssueInspectorPane();
                }
            });
        });
    }

    private void ScheduleIssueDetailPaneRealization()
    {
        if (_issueDetailPane is not null || !ViewModel.IsIssueContentVisible)
        {
            return;
        }

        int version = _workspaceChromeRealizationVersion;
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (version == _workspaceChromeRealizationVersion && ViewModel.IsIssueContentVisible)
            {
                EnsureIssueDetailPane();
            }
        });
    }

    private void EnsureIssueDetailPane()
    {
        if (_issueDetailPane is not null)
        {
            return;
        }

        RepoIssueDetailPane pane = new(ViewModel);
        pane.OpenListRequested += (_, _) => OpenIssueListPane();
        pane.OpenInspectorRequested += (_, _) => OpenIssueInspectorPane();
        pane.EditRequested += (sender, _) => EditIssueButton_Click(sender!, new RoutedEventArgs());
        pane.MetadataRequested += (sender, _) => MetadataButton_Click(sender!, new RoutedEventArgs());
        pane.ToggleStateRequested += (sender, _) => ToggleIssueStateButton_Click(sender!, new RoutedEventArgs());
        pane.CommentRequested += (sender, _) => CommentButton_Click(sender!, new RoutedEventArgs());
        pane.CommentActionRequested += IssueCommentActionRequested;
        _issueDetailPane = pane;
        IssueDetailPanePresenter.Content = pane;
        pane.UpdateResponsiveState(IssuesWorkspace.State);
    }

    private void EnsureIssueListPane()
    {
        if (_issueListPane is not null)
        {
            return;
        }

        RepoIssueListPane pane = new(ViewModel);
        pane.CloseRequested += (_, _) => IssuesWorkspace.CloseDrawer();
        pane.IssueSelectionPriming += (_, args) =>
        {
            EnsureIssueDetailPane();
            _issueDetailPane?.PrimeIssueSelection(args.Issue);
        };
        pane.IssueSelected += (_, args) =>
        {
            if (IssuesWorkspace.IsLeadingDrawerOpen)
            {
                ListViewScrollAnchor anchor = pane.CaptureScrollAnchor();
                IssuesWorkspace.CloseDrawer();
                anchor.RestoreAcrossLayoutPasses(DispatcherQueue);
            }
            ScheduleIssueTraversalCommit(args.Issue);
        };
        pane.NewIssueRequested += (sender, _) => NewIssueButton_Click(sender!, new RoutedEventArgs());
        _issueListPane = pane;
        IssueListPanePresenter.Content = pane;
        UpdatePaneButtonVisibility();
    }

    private void ScheduleIssueTraversalCommit(GitHubIssue issue)
    {
        if (!ProductPerformanceReadiness.IsEnabled)
        {
            return;
        }

        long generation = Interlocked.Increment(ref _issueTraversalGeneration);
        ProductPerformanceRenderCommitter.ScheduleAfterNextFrame(
            this,
            () => generation == Volatile.Read(ref _issueTraversalGeneration) && IsLoaded,
            () => _issueDetailPane?.IsIssueSelectionPrimed(issue) == true,
            () => ProductPerformanceReadiness.CommitTraversal(
                "repo_issues",
                issue.AutomationId));
    }

    private void EnsureIssueInspectorPane()
    {
        if (_issueInspectorPane is not null)
        {
            return;
        }

        RepoIssueInspectorPane pane = new(ViewModel);
        pane.CloseRequested += (_, _) => IssuesWorkspace.CloseDrawer();
        pane.MetadataRequested += (sender, _) => MetadataButton_Click(sender!, new RoutedEventArgs());
        _issueInspectorPane = pane;
        IssueInspectorPanePresenter.Content = pane;
        UpdatePaneButtonVisibility();
    }

    private async void ToggleIssueStateButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ToggleSelectedIssueStateAsync();
    }

    private async void CommentButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.AddIssueCommentAsync();
        _issueDetailPane?.CompleteCommentSubmission();
    }

    private async void IssueCommentActionRequested(object? sender, CommentActionRequestedEventArgs e)
    {
        if (sender is not CommentInteractionBar bar)
        {
            return;
        }

        bool isIssueBody = e.TargetKind == CommentTargetKind.Issue;
        switch (e.Action)
        {
            case CommentActionKind.ToggleReaction when !string.IsNullOrWhiteSpace(e.Value):
                if (isIssueBody)
                {
                    await ToggleSelectedIssueReactionAsync(e.Value);
                }
                else
                {
                    await ToggleIssueCommentReactionAsync(e.TargetId, e.Value);
                }
                break;
            case CommentActionKind.QuoteReply:
                ViewModel.IssueCommentDraft = CommentMarkdownFormatter.AppendQuote(
                    ViewModel.IssueCommentDraft,
                    bar.Body);
                _issueDetailPane?.OpenCommentComposer();
                break;
            case CommentActionKind.CopyLink:
                PlatformHelper.CopyString(bar.HtmlUrl);
                break;
            case CommentActionKind.CopyMarkdown:
                PlatformHelper.CopyString(bar.Body);
                break;
            case CommentActionKind.Edit:
                if (isIssueBody)
                {
                    EditIssueButton_Click(bar, new RoutedEventArgs());
                }
                else
                {
                    await ShowIssueCommentEditDialogAsync(e.TargetId, bar.Body);
                }
                break;
            case CommentActionKind.Pin:
                await ViewModel.SetIssueCommentPinnedAsync(e.TargetId, pinned: true);
                break;
            case CommentActionKind.Unpin:
                await ViewModel.SetIssueCommentPinnedAsync(e.TargetId, pinned: false);
                break;
            case CommentActionKind.Hide when !string.IsNullOrWhiteSpace(e.Value):
                await ViewModel.SetIssueCommentMinimizedAsync(bar.NodeId, e.Value);
                break;
            case CommentActionKind.Unhide:
                await ViewModel.SetIssueCommentMinimizedAsync(bar.NodeId, classifier: null);
                break;
            case CommentActionKind.Delete:
                await ShowIssueCommentDeleteDialogAsync(e.TargetId);
                break;
        }
    }

    private async Task ToggleSelectedIssueReactionAsync(string content)
    {
        IReadOnlyList<GitHubReaction>? reactions = await ViewModel.GetSelectedIssueReactionsAsync();
        if (reactions is null)
        {
            return;
        }

        Dictionary<string, long> viewerReactionIds = reactions
            .Where(reaction => string.Equals(reaction.User.Login, ViewModel.AuthenticatedLogin, StringComparison.OrdinalIgnoreCase))
            .GroupBy(static reaction => reaction.Content, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        HashSet<string> selected = viewerReactionIds.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!selected.Add(content))
        {
            selected.Remove(content);
        }

        await ViewModel.ApplySelectedIssueReactionSelectionAsync(selected, viewerReactionIds);
    }

    private async Task ToggleIssueCommentReactionAsync(long commentId, string content)
    {
        IReadOnlyList<GitHubReaction>? reactions = await ViewModel.GetIssueCommentReactionsAsync(commentId);
        if (reactions is null)
        {
            return;
        }

        Dictionary<string, long> viewerReactionIds = reactions
            .Where(reaction => string.Equals(reaction.User.Login, ViewModel.AuthenticatedLogin, StringComparison.OrdinalIgnoreCase))
            .GroupBy(static reaction => reaction.Content, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        HashSet<string> selected = viewerReactionIds.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!selected.Add(content))
        {
            selected.Remove(content);
        }

        await ViewModel.ApplyIssueCommentReactionSelectionAsync(commentId, selected, viewerReactionIds);
    }

    private async Task ShowIssueCommentEditDialogAsync(long commentId, string body)
    {
        GitHubIssueComment? comment = ViewModel.IssueComments.FirstOrDefault(item => item.Id == commentId);
        MarkdownForm form = new()
        {
            Text = body,
            DocumentSource = comment?.MarkdownSource,
            EditorHeight = 420
        };
        AutomationProperties.SetAutomationId(form, $"RepoIssuesComment_{commentId}_EditForm");
        AutomationProperties.SetName(form, L("RepoIssues/Dialogs/CommentEdit/BodyAutomationName", "Comment Markdown"));
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter("RepoIssuesCommentEditDialogError");
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = L("RepoIssues/Dialogs/CommentEdit/Title", "Edit comment"),
            Content = AppDialogStyleCatalog.CreateContentPanel(form, errorText),
            PrimaryButtonText = L("Common/Save", "Save"),
            CloseButtonText = L("Common/Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoIssuesCommentEditDialog");
        AutomationProperties.SetName(dialog, L("RepoIssues/Dialogs/CommentEdit/AutomationName", "Edit issue comment"));

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () => await ViewModel.UpdateIssueCommentAsync(commentId, form.Text)
                ? DialogMutationResult.Success()
                : DialogMutationResult.Failure(ViewModel.StatusText),
            errorText,
            layoutKind: AppDialogLayoutKind.Editor);
    }

    private async Task ShowIssueCommentDeleteDialogAsync(long commentId)
    {
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter("RepoIssuesCommentDeleteDialogError");
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = L("RepoIssues/Dialogs/CommentDelete/Title", "Delete comment?"),
            Content = AppDialogStyleCatalog.CreateContentPanel(
                new TextBlock
                {
                    Text = L("RepoIssues/Dialogs/CommentDelete/Message", "This comment will be permanently deleted."),
                    TextWrapping = TextWrapping.Wrap
                },
                errorText),
            PrimaryButtonText = L("Common/Delete", "Delete"),
            CloseButtonText = L("Common/Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoIssuesCommentDeleteDialog");
        AutomationProperties.SetName(dialog, L("RepoIssues/Dialogs/CommentDelete/AutomationName", "Delete issue comment"));

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () => await ViewModel.DeleteIssueCommentAsync(commentId)
                ? DialogMutationResult.Success()
                : DialogMutationResult.Failure(ViewModel.StatusText),
            errorText);
    }

    private async void EditIssueButton_Click(object sender, RoutedEventArgs e)
    {
        GitHubIssue? issue = ViewModel.SelectedIssue;
        if (issue is null || !ViewModel.CanEditIssue)
        {
            return;
        }

        TextBox titleBox = new()
        {
            Header = ViewModel.TitleHeaderText,
            Text = issue.Title,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        MarkdownForm bodyForm = new()
        {
            Text = issue.Body ?? string.Empty,
            DocumentSource = ViewModel.IssueBodyMarkdownSource,
            EditorHeight = 220
        };
        AutomationProperties.SetAutomationId(titleBox, "RepoIssuesEditTitleBox");
        AutomationProperties.SetName(titleBox, L("RepoIssues/Dialogs/IssueTitleAutomationName", "Issue title"));
        AutomationProperties.SetAutomationId(bodyForm, "RepoIssuesEditBodyForm");
        AutomationProperties.SetName(bodyForm, L("RepoIssues/Dialogs/IssueDescriptionAutomationName", "Issue description"));
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter("RepoIssuesEditDialogError");

        StackPanel content = new()
        {
            Spacing = 12,
            Children =
            {
                titleBox,
                bodyForm,
                errorText
            }
        };
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.FormatEditIssueDialogTitle(issue.Number),
            Content = content,
            PrimaryButtonText = ViewModel.SaveButtonText,
            CloseButtonText = ViewModel.CancelButtonText,
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoIssuesEditDialog");
        AutomationProperties.SetName(dialog, L("RepoIssues/Dialogs/Edit/AutomationName", "Edit issue"));

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                if (string.IsNullOrWhiteSpace(titleBox.Text))
                {
                    titleBox.Focus(FocusState.Programmatic);
                    return DialogMutationResult.Failure(ViewModel.EmptyTitleValidationText);
                }

                await ViewModel.UpdateSelectedIssueAsync(titleBox.Text.Trim(), bodyForm.Text);
                return ViewModel.LastDialogMutationSucceeded
                    ? DialogMutationResult.Success()
                    : DialogMutationResult.Failure(ViewModel.StatusText);
            },
            errorText,
            layoutKind: AppDialogLayoutKind.Editor);
    }

    private async void MetadataButton_Click(object sender, RoutedEventArgs e)
    {
        RepoIssuePageViewModel.IssueMetadataDialogData? data =
            await ViewModel.LoadSelectedIssueMetadataDialogDataAsync();
        if (data is null || ViewModel.SelectedIssue is null)
        {
            return;
        }

        ListView assigneesList = CreateMetadataList(
            ViewModel.AssigneesSectionTitle,
            "RepoIssuesMetadataAssigneesList",
            data.AvailableAssignees,
            nameof(GitHubActor.Login));
        SelectMatchingItems(
            assigneesList,
            data.AvailableAssignees,
            ViewModel.SelectedAssignees.Select(static actor => actor.Login),
            static actor => actor.Login);

        ListView labelsList = CreateMetadataList(
            ViewModel.LabelsSectionTitle,
            "RepoIssuesMetadataLabelsList",
            data.AvailableLabels,
            nameof(GitHubLabel.Name));
        SelectMatchingItems(
            labelsList,
            data.AvailableLabels,
            ViewModel.SelectedLabels.Select(static label => label.Name),
            static label => label.Name);

        HashSet<string> selectedAssigneeLogins = ViewModel.SelectedAssignees
            .Select(static actor => actor.Login)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> selectedLabelNames = ViewModel.SelectedLabels
            .Select(static label => label.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool applyingFilter = false;
        assigneesList.SelectionChanged += (_, args) =>
        {
            if (applyingFilter)
            {
                return;
            }

            foreach (GitHubActor actor in args.AddedItems.Cast<GitHubActor>())
            {
                selectedAssigneeLogins.Add(actor.Login);
            }
            foreach (GitHubActor actor in args.RemovedItems.Cast<GitHubActor>())
            {
                selectedAssigneeLogins.Remove(actor.Login);
            }
        };
        labelsList.SelectionChanged += (_, args) =>
        {
            if (applyingFilter)
            {
                return;
            }

            foreach (GitHubLabel label in args.AddedItems.Cast<GitHubLabel>())
            {
                selectedLabelNames.Add(label.Name);
            }
            foreach (GitHubLabel label in args.RemovedItems.Cast<GitHubLabel>())
            {
                selectedLabelNames.Remove(label.Name);
            }
        };

        TextBox metadataFilter = new()
        {
            PlaceholderText = L(
                "RepoIssues/Dialogs/Metadata/FilterPlaceholder",
                "Filter assignees and labels"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(metadataFilter, "RepoIssuesMetadataFilterBox");
        AutomationProperties.SetName(
            metadataFilter,
            L("RepoIssues/Dialogs/Metadata/FilterAutomationName", "Filter issue metadata"));
        metadataFilter.TextChanged += (_, _) =>
        {
            string query = metadataFilter.Text.Trim();
            applyingFilter = true;
            try
            {
                GitHubActor[] visibleAssignees = data.AvailableAssignees
                    .Where(actor => string.IsNullOrWhiteSpace(query) ||
                        actor.Login.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                GitHubLabel[] visibleLabels = data.AvailableLabels
                    .Where(label => string.IsNullOrWhiteSpace(query) ||
                        label.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                assigneesList.ItemsSource = visibleAssignees;
                labelsList.ItemsSource = visibleLabels;
                SelectMatchingItems(
                    assigneesList,
                    visibleAssignees,
                    selectedAssigneeLogins,
                    static actor => actor.Login);
                SelectMatchingItems(
                    labelsList,
                    visibleLabels,
                    selectedLabelNames,
                    static label => label.Name);
            }
            finally
            {
                applyingFilter = false;
            }
        };

        List<IssueMilestoneChoice> milestoneChoices =
        [
            new(null, ViewModel.NoMilestoneText),
            .. data.AvailableMilestones.Select(static milestone =>
                new IssueMilestoneChoice(milestone.Number, milestone.Title))
        ];
        ComboBox milestoneBox = new()
        {
            Header = ViewModel.MilestoneHeaderText,
            DisplayMemberPath = nameof(IssueMilestoneChoice.Title),
            ItemsSource = milestoneChoices,
            SelectedItem = milestoneChoices.FirstOrDefault(choice =>
                choice.Number == ViewModel.SelectedIssue.Milestone?.Number)
                ?? milestoneChoices[0],
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(milestoneBox, "RepoIssuesMetadataMilestonePicker");
        AutomationProperties.SetName(milestoneBox, L("RepoIssues/Dialogs/Metadata/MilestoneAutomationName", "Issue milestone"));
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter("RepoIssuesMetadataDialogError");

        StackPanel content = new()
        {
            MinWidth = 0,
            MaxWidth = 440,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 12,
            Children =
            {
                metadataFilter,
                assigneesList,
                labelsList,
                milestoneBox,
                errorText
            }
        };
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.FormatMetadataDialogTitle(ViewModel.SelectedIssue.Number),
            Content = content,
            PrimaryButtonText = ViewModel.SaveButtonText,
            CloseButtonText = ViewModel.CancelButtonText,
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoIssuesMetadataDialog");
        AutomationProperties.SetName(dialog, L("RepoIssues/Dialogs/Metadata/AutomationName", "Edit issue metadata"));
        dialog.Opened += async (_, _) =>
            _ = await FocusManager.TryFocusAsync(metadataFilter, FocusState.Keyboard);

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                IssueMilestoneChoice selectedMilestone =
                    (IssueMilestoneChoice?)milestoneBox.SelectedItem ?? milestoneChoices[0];
                await ViewModel.UpdateSelectedIssueMetadataAsync(new RepoIssuePageViewModel.IssueMetadataUpdate(
                    selectedAssigneeLogins.ToArray(),
                    selectedLabelNames.ToArray(),
                    selectedMilestone.Number));
                return ViewModel.LastDialogMutationSucceeded
                    ? DialogMutationResult.Success()
                    : DialogMutationResult.Failure(ViewModel.StatusText);
            },
            errorText);
    }

    private static ListView CreateMetadataList<T>(
        string header,
        string automationId,
        IReadOnlyList<T> items,
        string displayMemberPath)
    {
        ListView list = new()
        {
            Header = header,
            ItemsSource = items,
            DisplayMemberPath = displayMemberPath,
            MaxHeight = 150,
            IsMultiSelectCheckBoxEnabled = true,
            SelectionMode = ListViewSelectionMode.Multiple
        };
        AutomationProperties.SetAutomationId(list, automationId);
        AutomationProperties.SetName(list, header);
        return list;
    }

    private static void SelectMatchingItems<T>(
        ListView list,
        IReadOnlyList<T> available,
        IEnumerable<string> selectedKeys,
        Func<T, string> keySelector)
    {
        HashSet<string> keys = selectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (T item in available.Where(item => keys.Contains(keySelector(item))))
        {
            list.SelectedItems.Add(item);
        }
    }

    private async void NewIssueButton_Click(object sender, RoutedEventArgs e)
    {
        TextBox titleBox = new()
        {
            Header = L("RepoIssues/Dialogs/Create/TitleHeader", "Title"),
            PlaceholderText = L("RepoIssues/Dialogs/Create/TitlePlaceholder", "Issue title"),
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(titleBox, "RepoIssuesCreateTitleBox");
        AutomationProperties.SetName(titleBox, L("RepoIssues/Dialogs/IssueTitleAutomationName", "Issue title"));
        TextBox bodyBox = new()
        {
            Header = L("RepoIssues/Dialogs/Create/DescriptionHeader", "Description"),
            PlaceholderText = L("RepoIssues/Dialogs/Create/DescriptionPlaceholder", "Add a description..."),
            AcceptsReturn = true,
            Height = 180,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(bodyBox, "RepoIssuesCreateBodyBox");
        AutomationProperties.SetName(bodyBox, L("RepoIssues/Dialogs/IssueDescriptionAutomationName", "Issue description"));
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter("RepoIssuesCreateDialogError");
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
            Title = L("RepoIssues/Dialogs/Create/Title", "New issue"),
            Content = content,
            PrimaryButtonText = L("Common/Create", "Create"),
            CloseButtonText = L("Common/Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoIssuesCreateDialog");
        AutomationProperties.SetName(dialog, L("RepoIssues/Dialogs/Create/AutomationName", "Create issue"));

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                if (string.IsNullOrWhiteSpace(titleBox.Text))
                {
                    titleBox.Focus(FocusState.Programmatic);
                    return DialogMutationResult.Failure(ViewModel.EmptyTitleValidationText);
                }

                await ViewModel.CreateIssueAsync(titleBox.Text.Trim(), bodyBox.Text);
                return ViewModel.LastDialogMutationSucceeded
                    ? DialogMutationResult.Success()
                    : DialogMutationResult.Failure(ViewModel.StatusText);
            },
            errorText);
    }

    private static string L(string key, string fallback) =>
        LocalizedResourceText.GetString(key, fallback);

    private static string LF(string key, string fallback, params object?[] arguments) =>
        LocalizedResourceText.Format(key, fallback, arguments);

    private sealed record IssueMilestoneChoice(int? Number, string Title);
}
