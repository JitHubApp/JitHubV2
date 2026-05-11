using JitHub.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.Commit;

public sealed partial class CommitListDetailsItemPresenter : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item),
        typeof(object),
        typeof(CommitListDetailsItemPresenter),
        new PropertyMetadata(default(object), OnItemChanged));

    public static readonly DependencyProperty ShowHoverMenuProperty = DependencyProperty.Register(
        nameof(ShowHoverMenu),
        typeof(bool),
        typeof(CommitListDetailsItemPresenter),
        new PropertyMetadata(default(bool), OnShowHoverMenuChanged));

    private CommitItem? _commitItem;

    public object? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public bool ShowHoverMenu
    {
        get => (bool)GetValue(ShowHoverMenuProperty);
        set => SetValue(ShowHoverMenuProperty, value);
    }

    public CommitListDetailsItemPresenter()
    {
        InitializeComponent();
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommitListDetailsItemPresenter self)
        {
            self.Render();
        }
    }

    private static void OnShowHoverMenuChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommitListDetailsItemPresenter self && self._commitItem is not null)
        {
            self._commitItem.ShowHoverMenu = self.ShowHoverMenu;
        }
    }

    private void Render()
    {
        CommandableCommit? commit = ResolveCommit(Item);
        if (commit is null)
        {
            PresenterRoot.Children.Clear();
            _commitItem = null;
            return;
        }

        _commitItem ??= new CommitItem();
        _commitItem.ShowHoverMenu = ShowHoverMenu;
        _commitItem.ViewModel = commit;

        if (PresenterRoot.Children.Count == 0)
        {
            PresenterRoot.Children.Add(_commitItem);
        }
    }

    private static CommandableCommit? ResolveCommit(object? value)
        => value as CommandableCommit;
}
