using System;
using JitHub.Markdown.Links;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using Microsoft.Extensions.DependencyInjection;

namespace JitHub.WinUI.ViewModels.Pages;

/// <summary>
/// Opens supported GitHub links (users, repositories, issues and pull
/// requests) as shell workspace routes.
/// </summary>
internal sealed class ShellMarkdownRouteNavigator : IMarkdownRouteNavigator
{
    private readonly IServiceProvider _services;

    /// <param name="services">Resolves the shell on each activation, after it exists.</param>
    public ShellMarkdownRouteNavigator(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public bool TryOpen(Uri uri)
    {
        MarkdownGitHubRoute route = MarkdownLinkNavigationPolicy.ClassifyGitHubRoute(uri);
        if (route.Kind is not (
            MarkdownGitHubRouteKind.User or
            MarkdownGitHubRouteKind.Repository or
            MarkdownGitHubRouteKind.Issue or
            MarkdownGitHubRouteKind.PullRequest))
        {
            return false;
        }

        ShellPageViewModel? shell = _services.GetService<ShellPageViewModel>();
        if (shell is null)
        {
            return false;
        }

        ShellWorkspaceTabIdentity expectedRoute;
        if (route.Kind == MarkdownGitHubRouteKind.User)
        {
            expectedRoute = ShellWorkspaceTabIdentity.Profile(route.Owner!);
            shell.OpenUserProfile(route.Owner!, "markdown");
        }
        else if (route.Kind == MarkdownGitHubRouteKind.Repository)
        {
            expectedRoute = ShellWorkspaceTabIdentity.Repository(
                $"{route.Owner}/{route.Repository}",
                RepoPageType.CodePage,
                "main");
            shell.OpenRepositoryPage($"{route.Owner}/{route.Repository}", "code", null);
        }
        else
        {
            GitHubRepository repository = new()
            {
                FullName = $"{route.Owner}/{route.Repository}",
                Name = route.Repository!,
                DefaultBranch = "main",
                Owner = new GitHubRepositoryOwner { Login = route.Owner! }
            };
            if (route.Kind == MarkdownGitHubRouteKind.Issue)
            {
                expectedRoute = ShellWorkspaceTabIdentity.Repository(
                    repository,
                    RepoPageType.IssuePage,
                    repository.DefaultBranch);
                shell.OpenRepositoryTarget(
                    repository,
                    RepoPageType.IssuePage,
                    new IssueNavArg(repository, route.Number ?? 0));
            }
            else
            {
                expectedRoute = ShellWorkspaceTabIdentity.Repository(
                    repository,
                    RepoPageType.PullRequestPage,
                    repository.DefaultBranch);
                shell.OpenRepositoryTarget(
                    repository,
                    RepoPageType.PullRequestPage,
                    new PullRequestPageNavArg(repository, route.Number ?? 0));
            }
        }

        return shell.IsCurrentRoute(expectedRoute);
    }
}
