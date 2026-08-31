using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models;
using JitHub.Models.GitHub;
using JitHub.Security;
using JitHub.WinUI;

namespace JitHub.Services;

public sealed class AuthService : IAuthService
{
    internal const string PendingAuthStateSettingKey = "Auth.PendingState";
    internal const string ProtocolCallbackV3StatePrefix = OAuthHandoffProtocol.ProductionStatePrefix;
    internal const string DebugProtocolCallbackV3StatePrefix = OAuthHandoffProtocol.DevelopmentStatePrefix;

    private readonly IAppConfig _appConfigService;
    private readonly IAccountService _accountService;
    private readonly IGitHubClientService _gitHubClientService;
    private readonly IGitHubService _gitHubService;
    private readonly ISettingService _settingService;
    private readonly NavigationService _navigationService;
    private readonly IExternalUriLauncher _uriLauncher;
    private readonly IAuthCredentialStore _credentialStore;
    private readonly IAccountWorkQuiescence _accountWork;
    private readonly ITelemetryService _telemetryService;
    private readonly IAuthHandoffClient _authHandoffClient;
    private readonly SemaphoreSlim _authorizationGate = new(1, 1);
    private Task? _initializeTask;
    private string? _recentCompletedHandoff;
    private string? _recentCompletedState;
    private DateTimeOffset _recentCompletedExpiresAt;

    public AuthService(
        IAppConfig appConfigService,
        IAccountService accountService,
        IGitHubClientService gitHubClientService,
        IGitHubService gitHubService,
        ISettingService settingService,
        NavigationService navigationService,
        IExternalUriLauncher uriLauncher,
        IAuthCredentialStore credentialStore,
        IAccountWorkQuiescence accountWork,
        ITelemetryService telemetryService,
        IAuthHandoffClient authHandoffClient)
    {
        _appConfigService = appConfigService;
        _accountService = accountService;
        _gitHubClientService = gitHubClientService;
        _gitHubService = gitHubService;
        _settingService = settingService;
        _navigationService = navigationService;
        _uriLauncher = uriLauncher;
        _credentialStore = credentialStore;
        _accountWork = accountWork;
        _telemetryService = SafeTelemetryService.Wrap(telemetryService);
        _authHandoffClient = authHandoffClient;
    }

    public bool Authenticated { get; set; }

    public GitHubUser? AuthenticatedUser { get; set; }

    public AuthSessionRecoveryState RecoveryState { get; private set; }

    public Task InitializeAsync()
    {
        _initializeTask ??= RestoreSessionAsync();
        return _initializeTask;
    }

    public async Task Authenticate()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        TrackEvent(
            "auth.flow.started",
            AuthProperties(TelemetryTaxonomy.Sources.SignIn, TelemetryTaxonomy.Results.Started));
        try
        {
            await AuthenticateCoreAsync([]);
            stopwatch.Stop();
            TrackEvent(
                "auth.flow.completed",
                AuthProperties(
                    TelemetryTaxonomy.Sources.SignIn,
                    TelemetryTaxonomy.Results.Launched,
                    stopwatch.Elapsed));
        }
        catch (OperationCanceledException)
        {
            RecoveryState = AuthSessionRecoveryState.Cancelled;
            stopwatch.Stop();
            TrackFlowCompletion(
                TelemetryTaxonomy.Sources.SignIn,
                TelemetryTaxonomy.Results.Cancelled,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            TrackFlowCompletion(
                TelemetryTaxonomy.Sources.SignIn,
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                GetErrorKind(ex));
            TrackAuthError(TelemetryTaxonomy.Sources.SignIn, ex, stopwatch.Elapsed);
            throw;
        }
    }

    public async Task<bool> EnsureScopesAsync(params string[] scopes)
    {
        string[] requiredScopes = scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (requiredScopes.Length == 0)
        {
            TrackEvent(
                "auth.flow.completed",
                AuthProperties(
                    TelemetryTaxonomy.Sources.Scope,
                    TelemetryTaxonomy.Results.AlreadyGranted,
                    TimeSpan.Zero));
            return true;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        TrackEvent(
            "auth.flow.started",
            AuthProperties(TelemetryTaxonomy.Sources.Scope, TelemetryTaxonomy.Results.Started));
        long userId = AuthenticatedUser?.Id ?? _accountService.GetUser();
        string? token = GetToken(userId);
        try
        {
            OAuthAuthorizationResult result = await OAuthAuthorizationFlow.EnsureScopesAsync(
                _gitHubClientService,
                _uriLauncher,
                token,
                requiredScopes,
                () => CreateAuthorizationUri(requiredScopes));
            stopwatch.Stop();
            switch (result)
            {
                case OAuthAuthorizationResult.AlreadyGranted:
                    TrackEvent(
                        "auth.flow.completed",
                        AuthProperties(
                            TelemetryTaxonomy.Sources.Scope,
                            TelemetryTaxonomy.Results.AlreadyGranted,
                            stopwatch.Elapsed));
                    return true;
                case OAuthAuthorizationResult.AuthenticationRejected:
                    TrackEvent(
                        "auth.flow.completed",
                        AuthProperties(
                            TelemetryTaxonomy.Sources.Scope,
                            TelemetryTaxonomy.Results.Rejected,
                            stopwatch.Elapsed));
                    SignOut();
                    return false;
                case OAuthAuthorizationResult.LaunchFailed:
                    ClearPendingAuthState();
                    throw new InvalidOperationException("Unable to open the GitHub authorization page.");
                case OAuthAuthorizationResult.AuthorizationLaunched:
                    TrackEvent(
                        "auth.flow.completed",
                        AuthProperties(
                            TelemetryTaxonomy.Sources.Scope,
                            TelemetryTaxonomy.Results.Launched,
                            stopwatch.Elapsed));
                    return false;
                default:
                    throw new InvalidOperationException($"Unexpected OAuth authorization result: {result}.");
            }
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            TrackFlowCompletion(
                TelemetryTaxonomy.Sources.Scope,
                TelemetryTaxonomy.Results.Cancelled,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            TrackFlowCompletion(
                TelemetryTaxonomy.Sources.Scope,
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                GetErrorKind(ex));
            TrackAuthError(TelemetryTaxonomy.Sources.Scope, ex, stopwatch.Elapsed);
            throw;
        }
    }

    private async Task AuthenticateCoreAsync(IReadOnlyCollection<string> additionalScopes)
    {
        RecoveryState = AuthSessionRecoveryState.None;
        Uri oauthLoginUrl = CreateAuthorizationUri(additionalScopes);
        bool launched = await _uriLauncher.LaunchAsync(oauthLoginUrl);
        if (!launched)
        {
            ClearPendingAuthState();
            throw new InvalidOperationException("Unable to open the GitHub sign-in page.");
        }
    }

    private Uri CreateAuthorizationUri(IReadOnlyCollection<string> additionalScopes)
    {
        string? authState = GetPendingAuthState();
        string? verifier = _credentialStore.GetPendingVerifier();
        bool pendingPairIsValid =
            OAuthHandoffProtocol.TryGetChallenge(authState, out string challenge) &&
            !string.IsNullOrWhiteSpace(verifier) &&
            OAuthHandoffProtocol.Verify(verifier, challenge);
        if (!pendingPairIsValid)
        {
            authState = CreateAuthState(out verifier);
        }

        string stateToSave = authState!;
        SavePendingAuthState(stateToSave);
        _credentialStore.SavePendingVerifier(verifier!);

        Credential credential = _appConfigService.Credential;
        return _gitHubClientService.CreateLoginUri(
            credential.ClientId,
            stateToSave,
            credential.AuthorizationCallbackUrl,
            additionalScopes);
    }

    public async Task<bool> Authorize(string response)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        TrackEvent("auth.flow.started", AuthProperties("callback", "started"));
        await _authorizationGate.WaitAsync();
        try
        {
            bool authorized = await AuthorizeCoreAsync(response, stopwatch);
            if (authorized)
            {
                RecordCompletedCallback(response);
            }

            return authorized;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            TrackFlowCompletion(
                TelemetryTaxonomy.Sources.Callback,
                TelemetryTaxonomy.Results.Cancelled,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            TrackFlowCompletion(
                TelemetryTaxonomy.Sources.Callback,
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                GetErrorKind(ex));
            TrackAuthError("callback", ex, stopwatch.Elapsed);
            throw;
        }
        finally
        {
            _authorizationGate.Release();
        }
    }

    private async Task<bool> AuthorizeCoreAsync(string response, Stopwatch stopwatch)
    {
        string? handoff = GetQueryValue(response, "handoff");
        string? returnedState = GetQueryValue(response, "state");
        string? expectedState = GetPendingAuthState();
        string? verifier = _credentialStore.GetPendingVerifier();
        long persistedUserId = _accountService.GetUser();
        if (IsRecentCompletedCallback(handoff, returnedState))
        {
            RecoveryState = AuthSessionRecoveryState.None;
            stopwatch.Stop();
            TrackEvent("auth.flow.completed", AuthProperties("callback", "authenticated", stopwatch.Elapsed));
            return true;
        }

        bool stateMatches =
            !string.IsNullOrWhiteSpace(returnedState) &&
            !string.IsNullOrWhiteSpace(expectedState) &&
            string.Equals(returnedState, expectedState, StringComparison.Ordinal);
        bool hasValidPendingAuthorization =
            !string.IsNullOrWhiteSpace(expectedState) &&
            !string.IsNullOrWhiteSpace(verifier);
        if (string.IsNullOrWhiteSpace(handoff) || !hasValidPendingAuthorization || !stateMatches)
        {
            await RecoverSessionAfterAuthorizationFailureAsync(
                preservePendingAuthorization: hasValidPendingAuthorization);
            RecoveryState = AuthSessionRecoveryState.InvalidCallback;
            stopwatch.Stop();
            TrackFlowCompletion(
                TelemetryTaxonomy.Sources.Callback,
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.InvalidCallback);
            TrackAuthError("callback", "invalid_callback", stopwatch.Elapsed);
            return false;
        }

        string? token;
        try
        {
            token = await _authHandoffClient.RedeemAsync(
                _appConfigService.Credential.AuthorizationCallbackUrl,
                handoff!,
                expectedState!,
                verifier!);
        }
        catch (HttpRequestException)
        {
            ClearPendingAuthState();
            await RecoverSessionAfterAuthorizationFailureAsync();
            RecoveryState = AuthSessionRecoveryState.ServiceUnavailable;
            stopwatch.Stop();
            TrackFlowCompletion(
                TelemetryTaxonomy.Sources.Callback,
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Network);
            TrackAuthError(TelemetryTaxonomy.Sources.Callback, TelemetryTaxonomy.ErrorKinds.Network, stopwatch.Elapsed);
            return false;
        }
        catch (OperationCanceledException)
        {
            ClearPendingAuthState();
            await RecoverSessionAfterAuthorizationFailureAsync();
            RecoveryState = AuthSessionRecoveryState.ServiceUnavailable;
            stopwatch.Stop();
            TrackFlowCompletion(
                TelemetryTaxonomy.Sources.Callback,
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Network);
            return false;
        }
        catch (InvalidOperationException)
        {
            token = null;
        }

        ClearPendingAuthState();
        if (string.IsNullOrWhiteSpace(token))
        {
            await RecoverSessionAfterAuthorizationFailureAsync();
            RecoveryState = AuthSessionRecoveryState.InvalidCallback;
            stopwatch.Stop();
            TrackFlowCompletion(
                TelemetryTaxonomy.Sources.Callback,
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.InvalidCallback);
            TrackAuthError(TelemetryTaxonomy.Sources.Callback, TelemetryTaxonomy.ErrorKinds.InvalidCallback, stopwatch.Elapsed);
            return false;
        }

        bool hasExistingPersistedSession = persistedUserId > 0 && _credentialStore.GetAccountToken(persistedUserId) is not null;
        if (persistedUserId > 0 && !hasExistingPersistedSession)
        {
            // A newly redeemed token has no identity until /user resolves it. Do not leave a stale
            // account ID available to cache partitioning while that identity check is in flight.
            _accountService.RemoveUser();
        }

        SavePendingToken(token);

        try
        {
            GitHubUser user = await _gitHubClientService.GetCurrentUserAsync(token);
            SaveToken(token, user.Id);
            _accountService.SaveUser(user.Id);
            _accountWork.Activate(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AuthenticatedUser = user;
            Authenticated = true;
            RecoveryState = AuthSessionRecoveryState.None;
            _gitHubService.SetAccessToken(token);
            _initializeTask = Task.CompletedTask;
            stopwatch.Stop();
            TrackEvent("auth.flow.completed", AuthProperties("callback", "authenticated", stopwatch.Elapsed));
            return true;
        }
        catch (GitHubAuthenticationException)
        {
            RemovePendingToken();
            await RecoverSessionAfterAuthorizationFailureAsync();
            RecoveryState = AuthSessionRecoveryState.Expired;
            stopwatch.Stop();
            TrackFlowCompletion(
                TelemetryTaxonomy.Sources.Callback,
                TelemetryTaxonomy.Results.AuthError,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Authentication);
            TrackAuthError("callback", "authentication", stopwatch.Elapsed);
            return false;
        }
        catch (GitHubApiException)
        {
            if (hasExistingPersistedSession)
            {
                RemovePendingToken();
                await RecoverSessionAfterAuthorizationFailureAsync();
                stopwatch.Stop();
                TrackFlowCompletion(
                    TelemetryTaxonomy.Sources.Callback,
                    TelemetryTaxonomy.Results.Error,
                    stopwatch.Elapsed,
                    TelemetryTaxonomy.ErrorKinds.Api);
                TrackAuthError("callback", "api", stopwatch.Elapsed);
                return false;
            }

            _gitHubService.SetAccessToken(token);
            Authenticated = false;
            AuthenticatedUser = null;
            _initializeTask = null;
            stopwatch.Stop();
            TrackEvent("auth.flow.completed", AuthProperties("callback", "deferred", stopwatch.Elapsed));
            return true;
        }
        catch (HttpRequestException)
        {
            if (hasExistingPersistedSession)
            {
                RemovePendingToken();
                await RecoverSessionAfterAuthorizationFailureAsync();
                stopwatch.Stop();
                TrackFlowCompletion(
                    TelemetryTaxonomy.Sources.Callback,
                    TelemetryTaxonomy.Results.Error,
                    stopwatch.Elapsed,
                    TelemetryTaxonomy.ErrorKinds.Network);
                TrackAuthError("callback", "network", stopwatch.Elapsed);
                return false;
            }

            _gitHubService.SetAccessToken(token);
            Authenticated = false;
            AuthenticatedUser = null;
            _initializeTask = null;
            stopwatch.Stop();
            TrackEvent("auth.flow.completed", AuthProperties("callback", "deferred", stopwatch.Elapsed));
            return true;
        }
    }

    public async Task<GitHubUser?> RefreshAuthenticatedUserAsync()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        TrackEvent(
            "auth.action.executed",
            AuthProperties(
                TelemetryTaxonomy.Sources.User,
                TelemetryTaxonomy.Results.Started,
                action: TelemetryTaxonomy.Actions.RefreshUser));
        string? token = GetToken(AuthenticatedUser?.Id ?? _accountService.GetUser());
        if (string.IsNullOrWhiteSpace(token))
        {
            if (Authenticated)
            {
                ClearAuthenticationState(clearPersistedSession: false);
            }

            stopwatch.Stop();
            TrackEvent("auth.session.loaded", AuthProperties("refresh", "no_session", stopwatch.Elapsed));
            TrackAuthAction(
                TelemetryTaxonomy.Actions.RefreshUser,
                TelemetryTaxonomy.Results.NoSession,
                stopwatch.Elapsed);
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
                stopwatch.Stop();
                TrackEvent("auth.session.loaded", AuthProperties("refresh", "preview", stopwatch.Elapsed));
                TrackAuthAction(
                    TelemetryTaxonomy.Actions.RefreshUser,
                    TelemetryTaxonomy.Results.Success,
                    stopwatch.Elapsed);
                return previewUser;
            }

            GitHubUser user = await _gitHubClientService.GetCurrentUserAsync(token);
            SaveToken(token, user.Id);
            _accountService.SaveUser(user.Id);
            _accountWork.Activate(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AuthenticatedUser = user;
            Authenticated = true;
            RecoveryState = AuthSessionRecoveryState.None;
            stopwatch.Stop();
            TrackEvent("auth.session.loaded", AuthProperties("refresh", "success", stopwatch.Elapsed));
            TrackAuthAction(
                TelemetryTaxonomy.Actions.RefreshUser,
                TelemetryTaxonomy.Results.Success,
                stopwatch.Elapsed);
            return user;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            TrackAuthAction(
                TelemetryTaxonomy.Actions.RefreshUser,
                TelemetryTaxonomy.Results.Cancelled,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Cancelled);
            throw;
        }
        catch (GitHubAuthenticationException)
        {
            SignOut();
            RecoveryState = AuthSessionRecoveryState.Expired;
            stopwatch.Stop();
            TrackAuthAction(
                TelemetryTaxonomy.Actions.RefreshUser,
                TelemetryTaxonomy.Results.AuthError,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Authentication);
            TrackAuthError("refresh", "authentication", stopwatch.Elapsed);
            return null;
        }
        catch (GitHubApiException)
        {
            stopwatch.Stop();
            TrackAuthAction(
                TelemetryTaxonomy.Actions.RefreshUser,
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Api);
            TrackAuthError("refresh", "api", stopwatch.Elapsed);
            return AuthenticatedUser;
        }
        catch (HttpRequestException)
        {
            stopwatch.Stop();
            TrackAuthAction(
                TelemetryTaxonomy.Actions.RefreshUser,
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Network);
            TrackAuthError("refresh", "network", stopwatch.Elapsed);
            return AuthenticatedUser;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            TrackAuthAction(
                TelemetryTaxonomy.Actions.RefreshUser,
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                GetErrorKind(ex));
            TrackAuthError(TelemetryTaxonomy.Sources.Refresh, ex, stopwatch.Elapsed);
            throw;
        }
    }

    private bool IsRecentCompletedCallback(string? handoff, string? state) =>
        !string.IsNullOrWhiteSpace(handoff) &&
        !string.IsNullOrWhiteSpace(state) &&
        (Authenticated || CheckAuth(_accountService.GetUser())) &&
        DateTimeOffset.UtcNow <= _recentCompletedExpiresAt &&
        string.Equals(handoff, _recentCompletedHandoff, StringComparison.Ordinal) &&
        string.Equals(state, _recentCompletedState, StringComparison.Ordinal);

    private void RecordCompletedCallback(string response)
    {
        string? handoff = GetQueryValue(response, "handoff");
        string? state = GetQueryValue(response, "state");
        if (string.IsNullOrWhiteSpace(handoff) || string.IsNullOrWhiteSpace(state))
        {
            return;
        }

        _recentCompletedHandoff = handoff;
        _recentCompletedState = state;
        _recentCompletedExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
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

        return _credentialStore.GetAccountToken(userId);
    }

    public bool CheckAuth(long userId)
    {
        return GetToken(userId) is not null;
    }

    public void SignOut()
    {
        Stopwatch duration = Stopwatch.StartNew();
        try
        {
            ClearAuthenticationState(clearPersistedSession: true);
            RecoveryState = AuthSessionRecoveryState.None;
            _navigationService.Unauthorized();
            TrackEvent(
                "auth.action.executed",
                AuthProperties("session", TelemetryTaxonomy.Results.Success, duration.Elapsed, "sign_out"));
        }
        catch (Exception ex)
        {
            TrackEvent(
                "auth.action.executed",
                AuthProperties("session", TelemetryTaxonomy.Results.Error, duration.Elapsed, "sign_out"));
            TrackAuthError("session", ex, duration.Elapsed);
            throw;
        }
    }

    private async Task RestoreSessionAsync()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        long userId = _accountService.GetUser();
        string? token = userId > 0
            ? _credentialStore.GetAccountToken(userId)
            : GetPendingToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            if (userId > 0)
            {
                _accountService.RemoveUser();
            }

            // Preserve any in-flight browser sign-in so startup restore doesn't erase the callback state.
            ClearAuthenticationState(
                clearPersistedSession: false,
                preservePendingAuthorization: HasPendingAuthorization());
            stopwatch.Stop();
            TrackEvent("auth.session.loaded", AuthProperties("startup", "no_session", stopwatch.Elapsed));
            return;
        }

        // Make the token available to startup data loaders immediately while we validate/refresh the session.
        _gitHubService.SetAccessToken(token);

        try
        {
            AuthenticatedUser = await _gitHubClientService.GetCurrentUserAsync(token);
            if (userId <= 0)
            {
                SaveToken(token, AuthenticatedUser.Id);
                _accountService.SaveUser(AuthenticatedUser.Id);
            }

            _accountWork.Activate(AuthenticatedUser.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Authenticated = true;
            RecoveryState = AuthSessionRecoveryState.None;
            _initializeTask = Task.CompletedTask;
            stopwatch.Stop();
            TrackEvent("auth.session.loaded", AuthProperties("startup", "success", stopwatch.Elapsed));
        }
        catch (GitHubAuthenticationException)
        {
            ClearAuthenticationState(
                clearPersistedSession: true,
                preservePendingAuthorization: HasPendingAuthorization());
            RecoveryState = AuthSessionRecoveryState.Expired;
            stopwatch.Stop();
            TrackAuthError("startup", "authentication", stopwatch.Elapsed);
        }
        catch (GitHubApiException)
        {
            Authenticated = false;
            AuthenticatedUser = null;
            _gitHubService.SetAccessToken(token);
            _initializeTask = null;
            RecoveryState = AuthSessionRecoveryState.ServiceUnavailable;
            stopwatch.Stop();
            TrackAuthError("startup", "api", stopwatch.Elapsed);
        }
        catch (HttpRequestException)
        {
            Authenticated = false;
            AuthenticatedUser = null;
            _gitHubService.SetAccessToken(token);
            _initializeTask = null;
            RecoveryState = AuthSessionRecoveryState.Offline;
            stopwatch.Stop();
            TrackAuthError("startup", "network", stopwatch.Elapsed);
        }
    }

    private void TrackFlowCompletion(
        string source,
        string result,
        TimeSpan duration,
        string? errorKind = null)
    {
        Dictionary<string, string?> properties = AuthProperties(source, result, duration);
        properties["error_kind"] = errorKind;
        TrackEvent("auth.flow.completed", properties);
    }

    private void TrackAuthAction(
        string action,
        string result,
        TimeSpan duration,
        string? errorKind = null)
    {
        Dictionary<string, string?> properties = AuthProperties(
            TelemetryTaxonomy.Sources.User,
            result,
            duration,
            action);
        properties["error_kind"] = errorKind;
        TrackEvent("auth.action.executed", properties);
    }

    private void TrackAuthError(string source, Exception exception, TimeSpan duration) =>
        TrackAuthError(source, GetErrorKind(exception), duration);

    private void TrackAuthError(string source, string errorKind, TimeSpan duration)
    {
        TrackEvent("auth.error", new Dictionary<string, string?>
        {
            ["page"] = "auth",
            ["source"] = source,
            ["result"] = TelemetryTaxonomy.Results.Error,
            ["error_kind"] = errorKind,
            ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
        });
    }

    private void TrackEvent(string name, IReadOnlyDictionary<string, string?> properties)
    {
        try
        {
            _telemetryService.TrackEvent(name, properties);
        }
        catch
        {
            // Authentication must never depend on diagnostics or Store telemetry availability.
        }
    }

    private static Dictionary<string, string?> AuthProperties(
        string source,
        string result,
        TimeSpan? duration = null,
        string? action = null)
    {
        Dictionary<string, string?> properties = new()
        {
            ["page"] = "auth",
            ["source"] = source,
            ["result"] = result
        };
        if (duration is not null)
        {
            properties["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration.Value);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            properties["action"] = action;
        }

        return properties;
    }

    private static string GetErrorKind(Exception exception) => exception switch
    {
        GitHubAuthenticationException => "authentication",
        GitHubApiException => "api",
        HttpRequestException => "network",
        InvalidOperationException => "launch",
        OperationCanceledException => "canceled",
        _ => "unexpected"
    };

    private void SaveToken(string token, long userId)
    {
        _credentialStore.SaveAccountToken(userId, token);
        RemovePendingToken();
    }

    private void SavePendingToken(string token)
    {
        _credentialStore.SavePendingToken(token);
    }

    private string? GetPendingAuthState()
    {
        string? pendingState = _settingService.Get<string>(PendingAuthStateSettingKey);
        if (!string.IsNullOrWhiteSpace(pendingState))
        {
            return pendingState;
        }

        return _credentialStore.GetPendingState();
    }

    private void SavePendingAuthState(string authState)
    {
        _settingService.Save(PendingAuthStateSettingKey, authState);
        _credentialStore.SavePendingState(authState);
    }

    private void RemoveToken(long userId)
    {
        _credentialStore.RemoveAccountToken(userId);
    }

    private string? GetPendingToken()
    {
        return _credentialStore.GetPendingToken();
    }

    private void RemovePendingToken()
    {
        _credentialStore.RemovePendingToken();
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
            _recentCompletedHandoff = null;
            _recentCompletedState = null;
            _recentCompletedExpiresAt = default;
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
               !string.IsNullOrWhiteSpace(_credentialStore.GetPendingVerifier()) ||
               !string.IsNullOrWhiteSpace(GetPendingToken());
    }

    private async Task RecoverSessionAfterAuthorizationFailureAsync(bool preservePendingAuthorization = false)
    {
        if (!preservePendingAuthorization)
        {
            ClearPendingAuthState();
        }

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
        _credentialStore.RemovePendingVerifier();
    }

    private void RemovePendingAuthStateCredential()
    {
        _credentialStore.RemovePendingState();
    }

    private static string CreateAuthState(out string verifier)
    {
        return OAuthHandoffProtocol.CreateState(
#if DEBUG
            development: true,
#else
            development: false,
#endif
            out verifier);
    }

    internal static string GetProtocolCallbackStatePrefix()
    {
#if DEBUG
        return DebugProtocolCallbackV3StatePrefix;
#else
        return ProtocolCallbackV3StatePrefix;
#endif
    }

    private static string? GetQueryValue(string query, string key)
    {
        string trimmedQuery = WebUtility.HtmlDecode(query).TrimStart('?', '#', '/');
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return null;
        }

        string? match = null;
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
                if (match is not null)
                {
                    return null;
                }

                match = Uri.UnescapeDataString(keyValue[1]);
            }
        }

        return match;
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
