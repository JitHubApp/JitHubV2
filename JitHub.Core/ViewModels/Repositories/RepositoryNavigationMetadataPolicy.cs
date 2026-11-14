using JitHub.Core.Models.GitHub.Repositories;

namespace JitHub.Core.ViewModels.Repositories;

public static class RepositoryNavigationMetadataPolicy
{
    public static bool CanNavigateImmediately(GitHubRepository repository) =>
        !string.IsNullOrWhiteSpace(repository.Owner?.Login) &&
        !string.IsNullOrWhiteSpace(repository.Name) &&
        !string.IsNullOrWhiteSpace(repository.DefaultBranch);
}
