using JitHub.Models.GitHub;

namespace JitHub.WinUI.ViewModels.Common;

public static class RepositoryNavigationMetadataPolicy
{
    public static bool CanNavigateImmediately(GitHubRepository repository) =>
        !string.IsNullOrWhiteSpace(repository.Owner?.Login) &&
        !string.IsNullOrWhiteSpace(repository.Name) &&
        !string.IsNullOrWhiteSpace(repository.DefaultBranch);
}
