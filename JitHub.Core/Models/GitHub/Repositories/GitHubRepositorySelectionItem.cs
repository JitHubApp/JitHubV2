using CommunityToolkit.Mvvm.ComponentModel;

namespace JitHub.Core.Models.GitHub.Repositories;

[WinRT.GeneratedBindableCustomProperty]
public partial class GitHubRepositorySelectionItem : ObservableObject
{
    private bool _selected;

    public GitHubRepositorySelectionItem(GitHubRepository repository)
    {
        Repository = repository;
    }

    public GitHubRepository Repository { get; }

    public string AutomationId => $"RepositorySelection_{Repository.Id}";

    public string AutomationName => $"Select repository {Repository.FullName}";

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }
}
