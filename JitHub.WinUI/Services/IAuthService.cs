using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public enum AuthSessionRecoveryState
{
    None,
    Cancelled,
    InvalidCallback,
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

    Task<bool> Authorize(string response);

    Task<GitHubUser?> RefreshAuthenticatedUserAsync();

    string? GetToken(long userId);

    bool CheckAuth(long userId);

    void SignOut();
}
