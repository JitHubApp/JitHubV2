using System;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JitHub.WinUI.ViewModels.Base;

public abstract partial class RepoListViewModel<T> : LoadableViewModel<string>
{
    private List<T> _repos = [];
    [ObservableProperty]
    public partial List<T> PrivateRepos { get; set; } = [];

    [ObservableProperty]
    public partial List<T> PublicRepos { get; set; } = [];

    [ObservableProperty]
    public partial List<T> ForkedRepos { get; set; } = [];
    private bool _isEmpty;

    public List<T> Repos
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

    abstract public List<T> NewRepoList();

    // Override this to get different list of repos
    virtual public async Task LoadRepos()
    {
        Load(true);
        try
        {
            Repos = (await GetRepos()).ToList();
        }
        catch (InvalidOperationException ex) when (string.Equals(ex.Message, "GitHub access token is not available.", StringComparison.Ordinal))
        {
            Repos = NewRepoList();
            ForkedRepos = NewRepoList();
            PrivateRepos = NewRepoList();
            PublicRepos = NewRepoList();
            Load(false);
            return;
        }

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

