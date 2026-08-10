using System;
using JitHub.Services.Layout;
using JitHub.WinUI.ViewModels.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.Issue;

public sealed partial class RepoIssueDetailPane : UserControl
{
    private const double DefaultCommentEditorHeight = 130;
    private const double CompactLargeTextCommentEditorHeight = 64;
    private AdaptiveWorkspaceState? _responsiveState;

    public RepoIssuePageViewModel ViewModel { get; }

    public event EventHandler? OpenListRequested;
    public event EventHandler? OpenInspectorRequested;
    public event EventHandler? EditRequested;
    public event EventHandler? MetadataRequested;
    public event EventHandler? ReactionsRequested;
    public event EventHandler? ToggleStateRequested;
    public event EventHandler? CommentRequested;

    public RepoIssueDetailPane(RepoIssuePageViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public void UpdateResponsiveState(AdaptiveWorkspaceState? state)
    {
        _responsiveState = state;
        bool isLeadingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Leading;
        bool isTrailingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Trailing;
        bool useCompactActions = state?.Mode is AdaptiveWorkspaceMode.Narrow or AdaptiveWorkspaceMode.Compact;

        RepoIssuesOpenListPaneButton.Visibility = state?.ShouldShowLeadingPaneButton == true && !isLeadingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoIssuesOpenInspectorPaneButton.Visibility = state?.ShouldShowTrailingPaneButton == true && !isTrailingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoIssuesCompactOpenInspectorPaneButton.Visibility = state?.ShouldShowTrailingPaneButton == true && !isTrailingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoIssuesExpandedActionHost.Visibility = useCompactActions
            ? Visibility.Collapsed
            : Visibility.Visible;
        RepoIssuesCompactActionHost.Visibility = useCompactActions
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateCommentEditorHeight();
    }

    private void IssueCommentForm_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateCommentEditorHeight();

    private void UpdateCommentEditorHeight()
    {
        double editorHeight = Math.Max(1, IssueCommentForm.EditorHeight);
        double textScale = IssueCommentForm.EffectiveEditorHeight / editorHeight;
        bool isCompactWorkspace = _responsiveState?.Mode is AdaptiveWorkspaceMode.Narrow or AdaptiveWorkspaceMode.Compact;
        double desiredHeight = isCompactWorkspace && textScale >= 1.75
            ? CompactLargeTextCommentEditorHeight
            : DefaultCommentEditorHeight;
        if (Math.Abs(IssueCommentForm.EditorHeight - desiredHeight) > 0.01)
        {
            IssueCommentForm.EditorHeight = desiredHeight;
        }
    }

    private void IssueConversationScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double availableWidth = IssueConversationScrollViewer.ActualWidth
            - IssueConversationScrollViewer.Padding.Left
            - IssueConversationScrollViewer.Padding.Right;
        IssueConversationStackPanel.Width = double.IsFinite(availableWidth)
            ? Math.Max(0, availableWidth)
            : double.NaN;
    }

    private void OpenListPaneButton_Click(object sender, RoutedEventArgs e) =>
        OpenListRequested?.Invoke(this, EventArgs.Empty);

    private void OpenInspectorPaneButton_Click(object sender, RoutedEventArgs e) =>
        OpenInspectorRequested?.Invoke(this, EventArgs.Empty);

    private void EditIssueButton_Click(object sender, RoutedEventArgs e) =>
        EditRequested?.Invoke(this, EventArgs.Empty);

    private void MetadataButton_Click(object sender, RoutedEventArgs e) =>
        MetadataRequested?.Invoke(this, EventArgs.Empty);

    private void IssueReactionsButton_Click(object sender, RoutedEventArgs e) =>
        ReactionsRequested?.Invoke(this, EventArgs.Empty);

    private void ToggleIssueStateButton_Click(object sender, RoutedEventArgs e) =>
        ToggleStateRequested?.Invoke(this, EventArgs.Empty);

    private void CommentButton_Click(object sender, RoutedEventArgs e) =>
        CommentRequested?.Invoke(this, EventArgs.Empty);
}
