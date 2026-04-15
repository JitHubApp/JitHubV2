using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public interface IAuthService
{
    bool Authenticated { get; set; }

    GitHubUser? AuthenticatedUser { get; set; }

    Task InitializeAsync();

    Task Authenticate();

    Task<bool> Authorize(string response);

    Task<GitHubUser?> RefreshAuthenticatedUserAsync();

    string? GetToken(long userId);

    bool CheckAuth(long userId);

    void SignOut();
}
