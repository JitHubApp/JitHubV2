using JitHub.Models.LegacyGitHub;
using System.Windows.Input;

namespace JitHub.Models.NavArgs;

public class PullRequestConvPageNavArg
{
    public PullRequestConvPageNavArg(Repository repo, PullRequest pr, ICommand command)
    {
        Repository = repo;
        PullRequest = pr;
        RefreshCommand = command;
    }

    public Repository Repository { get; set; }

    public PullRequest PullRequest { get; set; }

    public ICommand RefreshCommand { get; set; }
}
