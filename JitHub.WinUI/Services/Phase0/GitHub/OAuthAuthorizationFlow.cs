using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public interface IExternalUriLauncher
{
    Task<bool> LaunchAsync(Uri uri, CancellationToken cancellationToken = default);
}

public sealed class WindowsExternalUriLauncher : IExternalUriLauncher
{
    public async Task<bool> LaunchAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        return await Windows.System.Launcher.LaunchUriAsync(uri);
    }
}

internal sealed class LoginLaunchFailureExternalUriLauncher : IExternalUriLauncher
{
    public Task<bool> LaunchAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<bool>(new System.Runtime.InteropServices.COMException(
            "Simulated unexpected URI launcher failure."));
    }
}

internal enum OAuthAuthorizationResult
{
    AlreadyGranted,
    AuthorizationLaunched,
    AuthenticationRejected,
    LaunchFailed
}

internal static class OAuthAuthorizationFlow
{
    public static async Task<OAuthAuthorizationResult> EnsureScopesAsync(
        IGitHubClientService gitHubClientService,
        IExternalUriLauncher uriLauncher,
        string? token,
        IReadOnlyCollection<string> requiredScopes,
        Func<Uri> authorizationUriFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gitHubClientService);
        ArgumentNullException.ThrowIfNull(uriLauncher);
        ArgumentNullException.ThrowIfNull(requiredScopes);
        ArgumentNullException.ThrowIfNull(authorizationUriFactory);

        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                IReadOnlySet<string> grantedScopes = await gitHubClientService
                    .GetTokenScopesAsync(token, cancellationToken)
                    .ConfigureAwait(false);
                if (OAuthScopePolicy.HasAll(grantedScopes, requiredScopes))
                {
                    return OAuthAuthorizationResult.AlreadyGranted;
                }
            }
            catch (GitHubAuthenticationException)
            {
                return OAuthAuthorizationResult.AuthenticationRejected;
            }
            catch (GitHubApiException)
            {
                // Personal access tokens and some GitHub proxies omit OAuth scope metadata.
                // An explicit authorization request is the safe fallback for destructive work.
            }
            catch (HttpRequestException)
            {
                throw;
            }
        }

        Uri authorizationUri = authorizationUriFactory();
        bool launched = await uriLauncher.LaunchAsync(authorizationUri, cancellationToken).ConfigureAwait(false);
        return launched
            ? OAuthAuthorizationResult.AuthorizationLaunched
            : OAuthAuthorizationResult.LaunchFailed;
    }
}
