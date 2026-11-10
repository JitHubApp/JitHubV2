namespace JitHub.Core.Services.Authentication;

/// <summary>
/// Receives the access token that GitHub requests should use. The
/// authentication service sets it whenever the signed-in session changes and
/// clears it with <see langword="null"/> on sign-out.
/// </summary>
public interface IGitHubAccessTokenSink
{
    void SetAccessToken(string? token);
}
