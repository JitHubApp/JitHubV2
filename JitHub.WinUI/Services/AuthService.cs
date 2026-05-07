using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using JitHub.Models;
using JitHub.Models.GitHub;
using JitHub.WinUI;
using Windows.Security.Credentials;

namespace JitHub.Services;

public sealed class AuthService : IAuthService
{
    private const uint ElementNotFoundHResult = 0x80070490;
    private const string PendingAuthStateSettingKey = "Auth.PendingState";
    private const string PendingAuthStateUserName = "__pending_state__";
    private const string PendingTokenUserName = "__pending__";
    internal const string ProtocolCallbackV2StatePrefix = "WINUI3V2_";

    private readonly IAppConfig _appConfigService;
    private readonly IAccountService _accountService;
    private readonly IGitHubClientService _gitHubClientService;
    private readonly IGitHubService _gitHubService;
    private readonly ISettingService _settingService;
    private readonly NavigationService _navigationService;
    private readonly PasswordVault _passwordVault = new();
    private Task? _initializeTask;

    public AuthService(
        IAppConfig appConfigService,
        IAccountService accountService,
        IGitHubClientService gitHubClientService,
        IGitHubService gitHubService,
        ISettingService settingService,
        NavigationService navigationService)
    {
        _appConfigService = appConfigService;
        _accountService = accountService;
        _gitHubClientService = gitHubClientService;
        _gitHubService = gitHubService;
        _settingService = settingService;
        _navigationService = navigationService;
    }

    public bool Authenticated { get; set; }

    public GitHubUser? AuthenticatedUser { get; set; }

    public Task InitializeAsync()
    {
        _initializeTask ??= RestoreSessionAsync();
        return _initializeTask;
    }

    public async Task Authenticate()
    {
        string? authState = GetPendingAuthState();
        if (string.IsNullOrWhiteSpace(authState))
        {
            authState = CreateAuthState();
        }

        SavePendingAuthState(authState);

        Credential credential = _appConfigService.Credential;
        Uri oauthLoginUrl = _gitHubClientService.CreateLoginUri(
            credential.ClientId,
            authState,
            credential.AuthorizationCallbackUrl);
        bool launched = await Windows.System.Launcher.LaunchUriAsync(oauthLoginUrl);
        if (!launched)
        {
            ClearPendingAuthState();
            throw new InvalidOperationException("Unable to open the GitHub sign-in page.");
        }
    }

    public async Task<bool> Authorize(string response)
    {
        string? token = GetQueryValue(response, "token");
        string? returnedState = GetQueryValue(response, "state");
        string? expectedState = GetPendingAuthState();
        long persistedUserId = _accountService.GetUser();
        bool stateMatches =
            !string.IsNullOrWhiteSpace(returnedState) &&
            !string.IsNullOrWhiteSpace(expectedState) &&
            string.Equals(returnedState, expectedState, StringComparison.Ordinal);
        bool allowLegacyMissingState =
            !string.IsNullOrWhiteSpace(token) &&
            string.IsNullOrWhiteSpace(returnedState) &&
            CanAcceptLegacyMissingStateCallback(expectedState);

        ClearPendingAuthState();

        if (string.IsNullOrWhiteSpace(token) ||
            string.IsNullOrWhiteSpace(expectedState) ||
            (!stateMatches && !allowLegacyMissingState))
        {
            await RecoverSessionAfterAuthorizationFailureAsync();
            return false;
        }

        bool hasExistingPersistedSession = persistedUserId > 0 && GetStoredToken(persistedUserId) is not null;
        SavePendingToken(token);

        try
        {
            GitHubUser user = await _gitHubClientService.GetCurrentUserAsync(token);
            SaveToken(token, user.Id);
            _accountService.SaveUser(user.Id);
            AuthenticatedUser = user;
            Authenticated = true;
            _gitHubService.SetAccessToken(token);
            _initializeTask = Task.CompletedTask;
            return true;
        }
        catch (GitHubAuthenticationException)
        {
            RemovePendingToken();
            await RecoverSessionAfterAuthorizationFailureAsync();
            return false;
        }
        catch (GitHubApiException)
        {
            if (hasExistingPersistedSession)
            {
                RemovePendingToken();
                await RecoverSessionAfterAuthorizationFailureAsync();
                return false;
            }

            _gitHubService.SetAccessToken(token);
            Authenticated = false;
            AuthenticatedUser = null;
            _initializeTask = null;
            return true;
        }
        catch (HttpRequestException)
        {
            if (hasExistingPersistedSession)
            {
                RemovePendingToken();
                await RecoverSessionAfterAuthorizationFailureAsync();
                return false;
            }

            _gitHubService.SetAccessToken(token);
            Authenticated = false;
            AuthenticatedUser = null;
            _initializeTask = null;
            return true;
        }
    }

    public async Task<GitHubUser?> RefreshAuthenticatedUserAsync()
    {
        string? token = GetToken(AuthenticatedUser?.Id ?? _accountService.GetUser());
        if (string.IsNullOrWhiteSpace(token))
        {
            if (Authenticated)
            {
                ClearAuthenticationState(clearPersistedSession: false);
            }

            return null;
        }

        try
        {
            _gitHubService.SetAccessToken(token);

            if (Program.CurrentLaunchOptions.IsPublicPreviewOverride && GitHubClientService.IsPublicAccessToken(token))
            {
                GitHubUser previewUser = CreatePublicPreviewUser();
                AuthenticatedUser = previewUser;
                Authenticated = true;
                return previewUser;
            }

            GitHubUser user = await _gitHubClientService.GetCurrentUserAsync(token);
            SaveToken(token, user.Id);
            _accountService.SaveUser(user.Id);
            AuthenticatedUser = user;
            Authenticated = true;
            return user;
        }
        catch (GitHubAuthenticationException)
        {
            SignOut();
            return null;
        }
        catch (GitHubApiException)
        {
            return AuthenticatedUser;
        }
        catch (HttpRequestException)
        {
            return AuthenticatedUser;
        }
    }

    private static GitHubUser CreatePublicPreviewUser() => new()
    {
        Id = 4_042_024,
        Login = "JitHubApp",
        Name = "JitHub",
        AvatarUrl = "https://avatars.githubusercontent.com/u/170190931",
        HtmlUrl = "https://github.com/JitHubApp",
        PublicRepos = 4
    };

    public string? GetToken(long userId)
    {
        if (Program.CurrentLaunchOptions.IsPublicPreviewOverride)
        {
            return GitHubClientService.PublicAccessToken;
        }

        if (userId <= 0)
        {
            return GetPendingToken();
        }

        string? token = GetStoredToken(userId);
        return string.IsNullOrWhiteSpace(token) ? GetPendingToken() : token;
    }

    private string? GetStoredToken(long userId)
    {
        try
        {
            PasswordCredential credential = _passwordVault.Retrieve(_appConfigService.Credential.ClientId, userId.ToString());
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception ex) when ((uint)ex.HResult == ElementNotFoundHResult)
        {
            return null;
        }
    }

    public bool CheckAuth(long userId)
    {
        return GetToken(userId) is not null;
    }

    public void SignOut()
    {
        ClearAuthenticationState(clearPersistedSession: true);
        _navigationService.Unauthorized();
    }

    private async Task RestoreSessionAsync()
    {
        long userId = _accountService.GetUser();
        string? token = GetToken(userId);
        if (string.IsNullOrWhiteSpace(token))
        {
            // Preserve any in-flight browser sign-in so startup restore doesn't erase the callback state.
            ClearAuthenticationState(
                clearPersistedSession: false,
                preservePendingAuthorization: HasPendingAuthorization());
            return;
        }

        // Make the token available to startup data loaders immediately while we validate/refresh the session.
        _gitHubService.SetAccessToken(token);

        try
        {
            AuthenticatedUser = await _gitHubClientService.GetCurrentUserAsync(token);
            Authenticated = true;
            _initializeTask = Task.CompletedTask;
        }
        catch (GitHubAuthenticationException)
        {
            ClearAuthenticationState(
                clearPersistedSession: true,
                preservePendingAuthorization: HasPendingAuthorization());
        }
        catch (GitHubApiException)
        {
            Authenticated = false;
            AuthenticatedUser = null;
            _gitHubService.SetAccessToken(token);
            _initializeTask = null;
        }
        catch (HttpRequestException)
        {
            Authenticated = false;
            AuthenticatedUser = null;
            _gitHubService.SetAccessToken(token);
            _initializeTask = null;
        }
    }

    private void SaveToken(string token, long userId)
    {
        string resource = _appConfigService.Credential.ClientId;
        RemoveToken(userId);
        RemovePendingToken();
        _passwordVault.Add(new PasswordCredential(resource, userId.ToString(), token));
    }

    private void SavePendingToken(string token)
    {
        string resource = _appConfigService.Credential.ClientId;
        RemovePendingToken();
        _passwordVault.Add(new PasswordCredential(resource, PendingTokenUserName, token));
    }

    private string? GetPendingAuthState()
    {
        string? pendingState = _settingService.Get<string>(PendingAuthStateSettingKey);
        if (!string.IsNullOrWhiteSpace(pendingState))
        {
            return pendingState;
        }

        try
        {
            PasswordCredential credential =
                _passwordVault.Retrieve(_appConfigService.Credential.ClientId, PendingAuthStateUserName);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception ex) when ((uint)ex.HResult == ElementNotFoundHResult)
        {
            return null;
        }
    }

    private void SavePendingAuthState(string authState)
    {
        _settingService.Save(PendingAuthStateSettingKey, authState);
        RemovePendingAuthStateCredential();
        _passwordVault.Add(new PasswordCredential(
            _appConfigService.Credential.ClientId,
            PendingAuthStateUserName,
            authState));
    }

    private void RemoveToken(long userId)
    {
        if (userId <= 0)
        {
            return;
        }

        try
        {
            PasswordCredential credential = _passwordVault.Retrieve(_appConfigService.Credential.ClientId, userId.ToString());
            _passwordVault.Remove(credential);
        }
        catch (Exception ex) when ((uint)ex.HResult == ElementNotFoundHResult)
        {
        }
    }

    private string? GetPendingToken()
    {
        try
        {
            PasswordCredential credential = _passwordVault.Retrieve(_appConfigService.Credential.ClientId, PendingTokenUserName);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception ex) when ((uint)ex.HResult == ElementNotFoundHResult)
        {
            return null;
        }
    }

    private void RemovePendingToken()
    {
        try
        {
            PasswordCredential credential = _passwordVault.Retrieve(_appConfigService.Credential.ClientId, PendingTokenUserName);
            _passwordVault.Remove(credential);
        }
        catch (Exception ex) when ((uint)ex.HResult == ElementNotFoundHResult)
        {
        }
    }

    private void ClearAuthenticationState(bool clearPersistedSession)
    {
        ClearAuthenticationState(clearPersistedSession, preservePendingAuthorization: false);
    }

    private void ClearAuthenticationState(bool clearPersistedSession, bool preservePendingAuthorization)
    {
        if (!preservePendingAuthorization)
        {
            ClearPendingAuthState();
            RemovePendingToken();
        }

        if (clearPersistedSession)
        {
            long userId = _accountService.GetUser();
            RemoveToken(userId);
            _accountService.RemoveUser();
        }

        Authenticated = false;
        AuthenticatedUser = null;
        _gitHubService.SetAccessToken(null);
        _initializeTask = Task.CompletedTask;
    }

    private bool HasPendingAuthorization()
    {
        return !string.IsNullOrWhiteSpace(GetPendingAuthState()) ||
               !string.IsNullOrWhiteSpace(GetPendingToken());
    }

    private static bool CanAcceptLegacyMissingStateCallback(string? expectedState)
    {
        // Older hosted callback pages can lose fragment-only state during browser -> protocol handoff.
        // Restrict this compatibility path to WinUI-initiated pending states so arbitrary token-only
        // protocol launches are still rejected unless they correspond to an active local sign-in flow.
        return !string.IsNullOrWhiteSpace(expectedState) &&
               expectedState.StartsWith(ProtocolCallbackV2StatePrefix, StringComparison.Ordinal);
    }

    private async Task RecoverSessionAfterAuthorizationFailureAsync()
    {
        ClearPendingAuthState();

        if (Authenticated && AuthenticatedUser is not null)
        {
            string? token = GetToken(AuthenticatedUser.Id);
            if (!string.IsNullOrWhiteSpace(token))
            {
                _gitHubService.SetAccessToken(token);
            }

            _initializeTask = Task.CompletedTask;
            return;
        }

        Authenticated = false;
        AuthenticatedUser = null;
        _gitHubService.SetAccessToken(null);
        _initializeTask = null;
        await InitializeAsync();
    }

    private void ClearPendingAuthState()
    {
        _settingService.Save<string?>(PendingAuthStateSettingKey, null);
        RemovePendingAuthStateCredential();
    }

    private void RemovePendingAuthStateCredential()
    {
        try
        {
            PasswordCredential credential =
                _passwordVault.Retrieve(_appConfigService.Credential.ClientId, PendingAuthStateUserName);
            _passwordVault.Remove(credential);
        }
        catch (Exception ex) when ((uint)ex.HResult == ElementNotFoundHResult)
        {
        }
    }

    private static string CreateAuthState()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return $"{ProtocolCallbackV2StatePrefix}{Convert.ToHexString(buffer)}";
    }

    private static string? GetQueryValue(string query, string key)
    {
        string trimmedQuery = WebUtility.HtmlDecode(query).TrimStart('?', '#', '/');
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return null;
        }

        foreach (string pair in trimmedQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] keyValue = pair.Split('=', 2, StringSplitOptions.None);
            if (keyValue.Length != 2)
            {
                continue;
            }

            string currentKey = NormalizeQueryKey(keyValue[0]);
            if (string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(keyValue[1]);
            }
        }

        return null;
    }

    private static string NormalizeQueryKey(string key)
    {
        string normalizedKey = Uri.UnescapeDataString(key).TrimStart('?', '#', '/');
        while (normalizedKey.StartsWith("amp;", StringComparison.OrdinalIgnoreCase))
        {
            normalizedKey = normalizedKey[4..].TrimStart('?', '#', '/');
        }

        return normalizedKey;
    }

}
