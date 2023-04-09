using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JitHub.ViewModels.Base;

public abstract partial class RepoListViewModel<T> : LoadableViewModel<string>
{
    private ICollection<T> _repos;
    [ObservableProperty]
    private ICollection<T> _privateRepos;
    [ObservableProperty]
    private ICollection<T> _publicRepos;
    [ObservableProperty]
    private ICollection<T> _forkedRepos;
    private bool _isEmpty;

    public ICollection<T> Repos
    {
        get => _repos;
        set
        {
            SetProperty(ref _repos, value);
            IsEmpty = !Loading && value.Count == 0;
        }
    }
    public bool IsEmpty
    {
        get => _isEmpty;
        set => SetProperty(ref _isEmpty, value);
    }

    public ICommand LoadCommand { get; }

    public RepoListViewModel()
    {
        LoadCommand = new AsyncRelayCommand(LoadRepos);
    }

    public void Load(bool value)
    {
        Loading = value;
        IsEmpty = !value && Repos.Count == 0;
    }

    abstract public Task<ICollection<T>> GetRepos();
    
    abstract public bool IsForked(T repo);
    abstract public bool IsPrivate(T repo);
    abstract public bool IsPublic(T repo);

    abstract public ICollection<T> NewRepoList();

    // Override this to get different list of repos
    virtual public async Task LoadRepos()
    {
        Load(true);
        Repos = await GetRepos();
        var forkedRepos = NewRepoList();
        var privateRepos = NewRepoList();
        var publicRepos = NewRepoList();
        foreach (var repo in Repos)
        {
            if (IsForked(repo))
            {
                forkedRepos.Add(repo);
            }
            else if (IsPrivate(repo))
            {
                privateRepos.Add(repo);
            }
            else if (IsPublic(repo))
            {
                publicRepos.Add(repo);
            }
        }
        ForkedRepos = forkedRepos;
        PrivateRepos = privateRepos;
        PublicRepos = publicRepos;
        Load(false);
    }
}
