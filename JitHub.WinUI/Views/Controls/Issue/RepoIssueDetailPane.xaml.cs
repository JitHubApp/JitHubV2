using System;
using System.Numerics;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using JitHub.Models.GitHub;
using JitHub.Services.Layout;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Controls.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace JitHub.WinUI.Views.Controls.Issue;

public sealed partial class RepoIssueDetailPane : UserControl
{
    private const double ShyHeaderStartOffset = 56;
    private const double ShyHeaderRestoreOffset = 8;
    private const double ShyHeaderRevealTravel = 64;
    private const double ShyHeaderRehideTravel = 24;
    private const double ScrollDirectionEpsilon = 0.5;
    private const double CompactShyHeaderContentInset = 74;
    private static readonly TimeSpan ShyHeaderForwardDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan ShyHeaderReverseDuration = TimeSpan.FromMilliseconds(220);
    private readonly TransitionHelper _headerTransition;
    private AdaptiveWorkspaceState? _responsiveState;
    private double _lastScrollOffset;
    private double _upwardRevealTravel;
    private double _downwardRehideTravel;
    private bool _headerRevealedByUpwardScroll;
    private bool _isScrollHeaderShy;
    private bool _isDetailHeaderShy;
    private int _headerTransitionGeneration;

    public RepoIssuePageViewModel ViewModel { get; }

    public event EventHandler? OpenListRequested;
    public event EventHandler? OpenInspectorRequested;
    public event EventHandler? EditRequested;
    public event EventHandler? MetadataRequested;
    public event EventHandler? ToggleStateRequested;
    public event EventHandler? CommentRequested;
    public event EventHandler<CommentActionRequestedEventArgs>? CommentActionRequested;

    public RepoIssueDetailPane(RepoIssuePageViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        _headerTransition = new TransitionHelper
        {
            Source = RepoIssuesDetailHeader,
            Target = RepoIssuesShyHeaderSurface,
            Duration = ShyHeaderForwardDuration,
            ReverseDuration = ShyHeaderReverseDuration,
            SourceToggleMethod = VisualStateToggleMethod.ByVisibility,
            TargetToggleMethod = VisualStateToggleMethod.ByVisibility,
            Configs =
            [
                new TransitionConfig { Id = "IssueHeaderSurface", ScaleMode = ScaleMode.ScaleY, EnableClipAnimation = true },
                new TransitionConfig { Id = "IssueTitle" },
                new TransitionConfig { Id = "IssueListButton" },
                new TransitionConfig { Id = "IssueActions" }
            ]
        };
    }

    public void UpdateResponsiveState(AdaptiveWorkspaceState? state)
    {
        _responsiveState = state;
        bool isLeadingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Leading;
        bool isTrailingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Trailing;

        Visibility listButtonVisibility = state?.ShouldShowLeadingPaneButton == true && !isLeadingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoIssuesOpenListPaneButton.Visibility = listButtonVisibility;
        RepoIssuesShyOpenListPaneButton.Visibility = listButtonVisibility;
        Visibility inspectorButtonVisibility = state?.ShouldShowTrailingPaneButton == true && !isTrailingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoIssuesOpenInspectorPaneButton.Visibility = inspectorButtonVisibility;
        RepoIssuesCompactOpenInspectorPaneButton.Visibility = inspectorButtonVisibility;
        RepoIssuesShyOpenInspectorPaneButton.Visibility = inspectorButtonVisibility;
        _isScrollHeaderShy = IssueConversationScrollViewer.VerticalOffset >= ShyHeaderStartOffset;
        _lastScrollOffset = IssueConversationScrollViewer.VerticalOffset;
        IssueConversationScrollViewer.Padding = IsCompactWorkspace
            ? new Thickness(18, CompactShyHeaderContentInset, 18, 18)
            : new Thickness(18);
        SetDetailHeaderShy(IsCompactWorkspace || _isScrollHeaderShy, animate: false);
    }

    public bool IsIssueApplied(GitHubIssue issue) =>
        ViewModel.SelectedIssue?.Number == issue.Number &&
        IsIssueSelectionPrimed(issue);

    public bool IsIssueSelectionPrimed(GitHubIssue issue) =>
        string.Equals(
            RepoIssuesDetailTitleText.Text,
            issue.Title,
            StringComparison.Ordinal);

    public void PrimeIssueSelection(GitHubIssue issue)
    {
        if (!string.Equals(RepoIssuesDetailTitleText.Text, issue.Title, StringComparison.Ordinal))
        {
            RepoIssuesDetailTitleText.Text = issue.Title;
        }

        if (!string.Equals(RepoIssuesShyDetailTitleText.Text, issue.Title, StringComparison.Ordinal))
        {
            RepoIssuesShyDetailTitleText.Text = issue.Title;
        }
    }

    private bool IsCompactWorkspace =>
        _responsiveState?.Mode is AdaptiveWorkspaceMode.Narrow or AdaptiveWorkspaceMode.Compact;

    private void IssueConversationScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double availableWidth = IssueConversationScrollViewer.ActualWidth
            - IssueConversationScrollViewer.Padding.Left
            - IssueConversationScrollViewer.Padding.Right;
        IssueConversationStackPanel.Width = double.IsFinite(availableWidth)
            ? Math.Max(0, availableWidth)
            : double.NaN;
    }

    private void IssueConversationScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (IsCompactWorkspace)
        {
            return;
        }

        double offset = IssueConversationScrollViewer.VerticalOffset;
        double delta = offset - _lastScrollOffset;
        _lastScrollOffset = offset;

        if (_isScrollHeaderShy)
        {
            if (offset <= ShyHeaderRestoreOffset)
            {
                RevealScrollHeader(revealedByUpwardScroll: false);
            }
            else if (delta < -ScrollDirectionEpsilon)
            {
                _upwardRevealTravel += -delta;
                if (_upwardRevealTravel >= ShyHeaderRevealTravel)
                {
                    RevealScrollHeader(revealedByUpwardScroll: true);
                }
            }
            else if (delta > ScrollDirectionEpsilon)
            {
                _upwardRevealTravel = 0;
            }

            return;
        }

        if (offset <= ShyHeaderRestoreOffset)
        {
            _headerRevealedByUpwardScroll = false;
            _downwardRehideTravel = 0;
        }
        else if (_headerRevealedByUpwardScroll)
        {
            if (delta > ScrollDirectionEpsilon)
            {
                _downwardRehideTravel += delta;
                if (_downwardRehideTravel >= ShyHeaderRehideTravel)
                {
                    HideScrollHeader();
                }
            }
            else if (delta < -ScrollDirectionEpsilon)
            {
                _downwardRehideTravel = 0;
            }
        }
        else if (offset >= ShyHeaderStartOffset)
        {
            HideScrollHeader();
        }
    }

    private void RevealScrollHeader(bool revealedByUpwardScroll)
    {
        _isScrollHeaderShy = false;
        _headerRevealedByUpwardScroll = revealedByUpwardScroll;
        _upwardRevealTravel = 0;
        _downwardRehideTravel = 0;
        SetDetailHeaderShy(IsCompactWorkspace, animate: true);
    }

    private void HideScrollHeader()
    {
        _isScrollHeaderShy = true;
        _headerRevealedByUpwardScroll = false;
        _upwardRevealTravel = 0;
        _downwardRehideTravel = 0;
        SetDetailHeaderShy(true, animate: true);
    }

    private void SetDetailHeaderShy(bool isShy, bool animate)
    {
        ApplyDetailHeaderChrome();
        if (_isDetailHeaderShy == isShy)
        {
            return;
        }

        _isDetailHeaderShy = isShy;
        int generation = ++_headerTransitionGeneration;
        if (!animate || !RepoIssuesDetailHeader.IsLoaded || !AreAnimationsEnabled())
        {
            _headerTransition.Reset(toInitialState: !isShy);
            RepoIssuesDetailLayout.UpdateLayout();
            ResetContentReflow();
            return;
        }

        _ = AnimateDetailHeaderAsync(isShy, generation);
    }

    private async Task AnimateDetailHeaderAsync(bool isShy, int generation)
    {
        try
        {
            bool reverseFromSettledShyState =
                !isShy && _headerTransition.IsTargetState && !_headerTransition.IsAnimating;
            double previousContentTop = reverseFromSettledShyState
                ? GetElementTop(IssueConversationScrollViewer, RepoIssuesDetailLayout)
                : 0;
            Task headerAnimation = isShy
                ? _headerTransition.StartAsync(forceUpdateAnimatedElements: true)
                : _headerTransition.ReverseAsync(forceUpdateAnimatedElements: true);

            RepoIssuesDetailLayout.UpdateLayout();
            if (isShy)
            {
                double reclaimedHeight = Math.Max(
                    0,
                    RepoIssuesDetailHeader.ActualHeight - RepoIssuesShyHeaderSurface.ActualHeight);
                AnimateContentReflow(
                    new Vector3(0, (float)-reclaimedHeight, 0),
                    ShyHeaderForwardDuration);
            }
            else if (reverseFromSettledShyState)
            {
                double expandedContentTop = GetElementTop(IssueConversationScrollViewer, RepoIssuesDetailLayout);
                SetContentReflowImmediately(new Vector3(0, (float)(previousContentTop - expandedContentTop), 0));
                AnimateContentReflow(Vector3.Zero, ShyHeaderReverseDuration);
            }

            else
            {
                AnimateContentReflow(Vector3.Zero, ShyHeaderReverseDuration);
            }

            await headerAnimation;
            if (generation != _headerTransitionGeneration)
            {
                return;
            }

            RepoIssuesDetailLayout.UpdateLayout();
            ResetContentReflow();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (generation != _headerTransitionGeneration)
        {
        }
        catch when (generation == _headerTransitionGeneration)
        {
            _headerTransition.Reset(toInitialState: !isShy);
            RepoIssuesDetailLayout.UpdateLayout();
            ResetContentReflow();
        }
    }

    private void AnimateContentReflow(Vector3 translation, TimeSpan duration)
    {
        IssueConversationScrollViewer.TranslationTransition = new Vector3Transition
        {
            Components = Vector3TransitionComponents.Y,
            Duration = duration
        };
        IssueConversationScrollViewer.Translation = translation;
    }

    private void SetContentReflowImmediately(Vector3 translation)
    {
        IssueConversationScrollViewer.TranslationTransition = null;
        IssueConversationScrollViewer.Translation = translation;
    }

    private void ResetContentReflow() =>
        SetContentReflowImmediately(Vector3.Zero);

    private static double GetElementTop(FrameworkElement element, UIElement relativeTo) =>
        element.TransformToVisual(relativeTo).TransformPoint(new Windows.Foundation.Point()).Y;

    private void ApplyDetailHeaderChrome()
    {
        RepoIssuesExpandedActionHost.Visibility = Visibility.Visible;
        RepoIssuesCompactActionHost.Visibility = Visibility.Collapsed;
    }

    private static bool AreAnimationsEnabled()
    {
        try
        {
            return new UISettings().AnimationsEnabled;
        }
        catch
        {
            return false;
        }
    }

    private void OpenListPaneButton_Click(object sender, RoutedEventArgs e) =>
        OpenListRequested?.Invoke(this, EventArgs.Empty);

    private void OpenInspectorPaneButton_Click(object sender, RoutedEventArgs e) =>
        OpenInspectorRequested?.Invoke(this, EventArgs.Empty);

    private void EditIssueButton_Click(object sender, RoutedEventArgs e) =>
        EditRequested?.Invoke(this, EventArgs.Empty);

    private void MetadataButton_Click(object sender, RoutedEventArgs e) =>
        MetadataRequested?.Invoke(this, EventArgs.Empty);

    private void ToggleIssueStateButton_Click(object sender, RoutedEventArgs e) =>
        ToggleStateRequested?.Invoke(this, EventArgs.Empty);

    private void IssueCommentFlyout_Opened(object sender, object e) =>
        _ = DispatcherQueue.TryEnqueue(() => IssueCommentForm.FocusEditor());

    private void IssueCommentFlyout_Closed(object sender, object e) =>
        RepoIssuesOpenCommentButton.Focus(FocusState.Programmatic);

    public void CompleteCommentSubmission()
    {
        if (string.IsNullOrWhiteSpace(ViewModel.IssueCommentDraft))
        {
            IssueCommentFlyout.Hide();
        }
    }

    public void OpenCommentComposer()
    {
        IssueCommentFlyout.ShowAt(RepoIssuesOpenCommentButton);
    }

    private void CommentButton_Click(object sender, RoutedEventArgs e) =>
        CommentRequested?.Invoke(this, EventArgs.Empty);

    private void CommentInteractionBar_ActionRequested(object? sender, CommentActionRequestedEventArgs e)
    {
        CommentActionRequested?.Invoke(sender ?? this, e);
    }
}
