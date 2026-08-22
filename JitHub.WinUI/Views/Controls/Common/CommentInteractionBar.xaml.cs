using System;
using System.Collections.Generic;
using JitHub.Models.GitHub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.Common;

public enum CommentTargetKind
{
    Issue,
    IssueComment,
    PullRequest,
    PullRequestComment,
    PullRequestReviewComment
}

public enum CommentActionKind
{
    ToggleReaction,
    QuoteReply,
    CopyLink,
    CopyMarkdown,
    Edit,
    Pin,
    Unpin,
    Hide,
    Unhide,
    Delete
}

public sealed class CommentActionRequestedEventArgs : EventArgs
{
    public CommentActionRequestedEventArgs(
        CommentTargetKind targetKind,
        CommentActionKind action,
        long targetId,
        string? value)
    {
        TargetKind = targetKind;
        Action = action;
        TargetId = targetId;
        Value = value;
    }

    public CommentTargetKind TargetKind { get; }
    public CommentActionKind Action { get; }
    public long TargetId { get; }
    public string? Value { get; }
}

public sealed partial class CommentInteractionBar : UserControl
{
    public static readonly DependencyProperty TargetKindProperty = Register(nameof(TargetKind), typeof(CommentTargetKind), CommentTargetKind.IssueComment);
    public static readonly DependencyProperty TargetIdProperty = Register(nameof(TargetId), typeof(long), 0L);
    public static readonly DependencyProperty NodeIdProperty = Register(nameof(NodeId), typeof(string), string.Empty);
    public static readonly DependencyProperty AuthorLoginProperty = Register(nameof(AuthorLogin), typeof(string), string.Empty);
    public static readonly DependencyProperty HtmlUrlProperty = Register(nameof(HtmlUrl), typeof(string), string.Empty);
    public static readonly DependencyProperty BodyProperty = Register(nameof(Body), typeof(string), string.Empty);
    public static readonly DependencyProperty ReactionsProperty = Register(nameof(Reactions), typeof(GitHubReactionSummary), null);
    public static readonly DependencyProperty ViewerLoginProperty = Register(nameof(ViewerLogin), typeof(string), string.Empty);
    public static readonly DependencyProperty CanReactProperty = Register(nameof(CanReact), typeof(bool), false);
    public static readonly DependencyProperty CanReplyProperty = Register(nameof(CanReply), typeof(bool), false);
    public static readonly DependencyProperty CanEditProperty = Register(nameof(CanEdit), typeof(bool), false);
    public static readonly DependencyProperty CanModerateProperty = Register(nameof(CanModerate), typeof(bool), false);
    public static readonly DependencyProperty IsPinnedProperty = Register(nameof(IsPinned), typeof(bool), false);
    public static readonly DependencyProperty IsMinimizedProperty = Register(nameof(IsMinimized), typeof(bool), false);

    public CommentInteractionBar()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateState();
    }

    public event EventHandler<CommentActionRequestedEventArgs>? ActionRequested;

    public CommentTargetKind TargetKind { get => (CommentTargetKind)GetValue(TargetKindProperty); set => SetValue(TargetKindProperty, value); }
    public long TargetId { get => (long)GetValue(TargetIdProperty); set => SetValue(TargetIdProperty, value); }
    public string NodeId { get => (string)GetValue(NodeIdProperty); set => SetValue(NodeIdProperty, value); }
    public string AuthorLogin { get => (string)GetValue(AuthorLoginProperty); set => SetValue(AuthorLoginProperty, value); }
    public string HtmlUrl { get => (string)GetValue(HtmlUrlProperty); set => SetValue(HtmlUrlProperty, value); }
    public string Body { get => (string)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public GitHubReactionSummary? Reactions { get => (GitHubReactionSummary?)GetValue(ReactionsProperty); set => SetValue(ReactionsProperty, value); }
    public string ViewerLogin { get => (string)GetValue(ViewerLoginProperty); set => SetValue(ViewerLoginProperty, value); }
    public bool CanReact { get => (bool)GetValue(CanReactProperty); set => SetValue(CanReactProperty, value); }
    public bool CanReply { get => (bool)GetValue(CanReplyProperty); set => SetValue(CanReplyProperty, value); }
    public bool CanEdit { get => (bool)GetValue(CanEditProperty); set => SetValue(CanEditProperty, value); }
    public bool CanModerate { get => (bool)GetValue(CanModerateProperty); set => SetValue(CanModerateProperty, value); }
    public bool IsPinned { get => (bool)GetValue(IsPinnedProperty); set => SetValue(IsPinnedProperty, value); }
    public bool IsMinimized { get => (bool)GetValue(IsMinimizedProperty); set => SetValue(IsMinimizedProperty, value); }

    public IReadOnlyList<GitHubReactionOption> ReactionOptions => GitHubReactionCatalog.Options;
    public string AddReactionEmoji => "\U0001F642";

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(CommentInteractionBar), new PropertyMetadata(defaultValue, OnPropertyChanged));

    private static void OnPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is CommentInteractionBar bar && bar.IsLoaded)
        {
            bar.UpdateState();
        }
    }

    private void UpdateState()
    {
        bool isAuthor = !string.IsNullOrWhiteSpace(ViewerLogin) &&
            string.Equals(ViewerLogin, AuthorLogin, StringComparison.OrdinalIgnoreCase);
        bool isBody = TargetKind is CommentTargetKind.Issue or CommentTargetKind.PullRequest;
        bool canEdit = CanEdit || isAuthor;
        bool canDelete = !isBody && (isAuthor || CanModerate);
        bool canPin = TargetKind == CommentTargetKind.IssueComment && CanModerate;
        bool canHide = !isBody && CanModerate && !string.IsNullOrWhiteSpace(NodeId);
        bool hasManagementAction = canEdit || canPin || canHide;

        ReactionItems.ItemsSource = Reactions?.Chips ?? [];
        AddReactionButton.Visibility = CanReact ? Visibility.Visible : Visibility.Collapsed;
        QuoteReplyItem.Visibility = CanReply ? Visibility.Visible : Visibility.Collapsed;
        EditItem.Visibility = canEdit ? Visibility.Visible : Visibility.Collapsed;
        PinItem.Visibility = canPin && !IsPinned ? Visibility.Visible : Visibility.Collapsed;
        UnpinItem.Visibility = canPin && IsPinned ? Visibility.Visible : Visibility.Collapsed;
        HideItem.Visibility = canHide && !IsMinimized ? Visibility.Visible : Visibility.Collapsed;
        UnhideItem.Visibility = canHide && IsMinimized
            ? Visibility.Visible
            : Visibility.Collapsed;
        ManagementSeparator.Visibility = hasManagementAction || canDelete
            ? Visibility.Visible
            : Visibility.Collapsed;
        DeleteSeparator.Visibility = canDelete && hasManagementAction
            ? Visibility.Visible
            : Visibility.Collapsed;
        DeleteItem.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReactionChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GitHubReactionChip chip })
        {
            Request(CommentActionKind.ToggleReaction, chip.Content);
        }
    }

    private void ReactionOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GitHubReactionOption option })
        {
            Request(CommentActionKind.ToggleReaction, option.Content);
        }
    }

    private void QuoteReplyItem_Click(object sender, RoutedEventArgs e) => Request(CommentActionKind.QuoteReply);
    private void CopyLinkItem_Click(object sender, RoutedEventArgs e) => Request(CommentActionKind.CopyLink, HtmlUrl);
    private void CopyMarkdownItem_Click(object sender, RoutedEventArgs e) => Request(CommentActionKind.CopyMarkdown, Body);
    private void EditItem_Click(object sender, RoutedEventArgs e) => Request(CommentActionKind.Edit, Body);
    private void PinItem_Click(object sender, RoutedEventArgs e) => Request(CommentActionKind.Pin);
    private void UnpinItem_Click(object sender, RoutedEventArgs e) => Request(CommentActionKind.Unpin);
    private void UnhideItem_Click(object sender, RoutedEventArgs e) => Request(CommentActionKind.Unhide);
    private void DeleteItem_Click(object sender, RoutedEventArgs e) => Request(CommentActionKind.Delete);

    private void HideItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string classifier })
        {
            Request(CommentActionKind.Hide, classifier);
        }
    }

    private void Request(CommentActionKind action, string? value = null) =>
        ActionRequested?.Invoke(this, new CommentActionRequestedEventArgs(TargetKind, action, TargetId, value));
}
