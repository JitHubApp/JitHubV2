using CommunityToolkit.Mvvm.ComponentModel;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public partial class GitHubRepositorySelectionItem : ObservableObject
{
    private bool _selected;

    public GitHubRepositorySelectionItem(GitHubRepository repository)
    {
        Repository = repository;
    }

    public GitHubRepository Repository { get; }

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }
}
