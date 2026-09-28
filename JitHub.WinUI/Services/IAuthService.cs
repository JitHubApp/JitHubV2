using System.Threading.Tasks;
using System.Threading;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public enum AuthSessionRecoveryState
{
    None,
    Cancelled,
    Expired,
    Offline,
    ServiceUnavailable
}

public interface IAuthService
{
    bool Authenticated { get; set; }

    GitHubUser? AuthenticatedUser { get; set; }

    AuthSessionRecoveryState RecoveryState => AuthSessionRecoveryState.None;

    Task InitializeAsync();

    Task Authenticate();

    Task<bool> EnsureScopesAsync(params string[] scopes);

    Task<GitHubUser?> RefreshAuthenticatedUserAsync();

    string? GetToken(long userId);

    Task<string?> GetValidTokenAsync(long userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(GetToken(userId));

    bool CheckAuth(long userId);

    void SignOut();
}
