using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Core.Services.Authentication;

public interface IDeviceAuthorizationPrompt
{
    Task<GitHubTokenSession?> AuthorizeAsync(
        string clientId,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default);
}
