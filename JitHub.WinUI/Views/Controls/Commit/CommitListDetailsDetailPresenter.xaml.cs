using JitHub.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.Commit;

public sealed partial class CommitListDetailsDetailPresenter : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item),
        typeof(object),
        typeof(CommitListDetailsDetailPresenter),
        new PropertyMetadata(default(object), OnItemChanged));

    public object? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public CommitListDetailsDetailPresenter()
    {
        InitializeComponent();
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommitListDetailsDetailPresenter self)
        {
            self.Render();
        }
    }

    private void Render()
    {
        CommandableCommit? commit = ResolveCommit(Item);
        if (commit is not null)
        {
            CommitDetailControl.Commit = commit;
        }
    }

    private static CommandableCommit? ResolveCommit(object? value)
        => value as CommandableCommit;
}
