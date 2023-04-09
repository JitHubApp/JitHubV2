using JitHub.Models;
using JitHub.Views.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.Views.Controls.Commit;

public sealed partial class CommitDetail : UserControl
{
    public static DependencyProperty CommitProperty = DependencyProperty.Register(
        nameof(Commit),
        typeof(CommandableCommit),
        typeof(CommitDetail),
        new PropertyMetadata(default(CommandableCommit), OnCommitChanged)
    );

    private static void OnCommitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommitDetail self && e.NewValue != null)
        {
            var commit = self.DataContext as CommandableCommit;
            self.DetailPageFrame.Navigate(typeof(RepoCommitDetailPage), commit);
        }
    }

    public CommandableCommit Commit
    {
        get => (CommandableCommit)GetValue(CommitProperty);
        set => SetValue(CommitProperty, value);
    }
    public CommitDetail()
    {
        this.InitializeComponent();
    }
}
