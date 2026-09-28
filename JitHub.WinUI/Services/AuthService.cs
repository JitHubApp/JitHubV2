using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models;
using JitHub.Models.GitHub;
using JitHub.WinUI;

namespace JitHub.Services;

public sealed class AuthService : IAuthService
{
    private readonly IAppConfig _appConfigService;
    private readonly IAccountService _accountService;
    private readonly IGitHubClientService _gitHubClientService;
    private readonly IGitHubService _gitHubService;
    private readonly NavigationService _navigationService;
    private readonly IAuthCredentialStore _credentialStore;
    private readonly IAccountWorkQuiescence _accountWork;
    private readonly ITelemetryService _telemetryService;
    private readonly IDeviceAuthorizationPrompt _devicePrompt;
    private readonly IGitHubDeviceFlowClient _deviceClient;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _proactiveRefreshGate = new();
    private Task? _proactiveRefreshTask;
    private long _proactiveRefreshUserId;
    private DateTimeOffset _nextProactiveRefreshAt;
    private Task? _initializeTask;
    private long _sessionGeneration;

    public AuthService(
        IAppConfig appConfigService,
        IAccountService accountService,
        IGitHubClientService gitHubClientService,
        IGitHubService gitHubService,
        NavigationService navigationService,
        IAuthCredentialStore credentialStore,
        IAccountWorkQuiescence accountWork,
        ITelemetryService telemetryService,
        IDeviceAuthorizationPrompt devicePrompt,
        IGitHubDeviceFlowClient deviceClient)
    {
        _appConfigService = appConfigService;
        _accountService = accountService;
        _gitHubClientService = gitHubClientService;
        _gitHubService = gitHubService;
        _navigationService = navigationService;
        _credentialStore = credentialStore;
        _accountWork = accountWork;
        _telemetryService = SafeTelemetryService.Wrap(telemetryService);
        _devicePrompt = devicePrompt;
        _deviceClient = deviceClient;
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
        AuthSessionRecoveryState priorRecoveryState = RecoveryState;
        long priorUserId = _accountService.GetUser();
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
                    TelemetryTaxonomy.Results.Success,
                    stopwatch.Elapsed));
        }
        catch (OperationCanceledException exception) when (IsTransportTimeout(exception))
        {
            RestoreSavedSessionAfterFailedSignIn(priorRecoveryState, priorUserId, AuthSessionRecoveryState.None);
            stopwatch.Stop();
            TrackFlowCompletion(
                TelemetryTaxonomy.Sources.SignIn,
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Network);
            TrackAuthError(TelemetryTaxonomy.Sources.SignIn, "network", stopwatch.Elapsed);
            throw new HttpRequestException("GitHub sign-in timed out.", exception);
        }
        catch (OperationCanceledException)
        {
            RestoreSavedSessionAfterFailedSignIn(priorRecoveryState, priorUserId, AuthSessionRecoveryState.Cancelled);
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
            RestoreSavedSessionAfterFailedSignIn(priorRecoveryState, priorUserId, AuthSessionRecoveryState.None);
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
        string? token = await GetValidTokenAsync(userId);
        try
        {
            IReadOnlySet<string> granted = string.IsNullOrWhiteSpace(token)
                ? new HashSet<string>(StringComparer.Ordinal)
                : await _gitHubClientService.GetTokenScopesAsync(token);
            if (!string.IsNullOrWhiteSpace(token) && OAuthScopePolicy.HasAll(granted, requiredScopes))
            {
                stopwatch.Stop();
                TrackEvent("auth.flow.completed", AuthProperties(
                    TelemetryTaxonomy.Sources.Scope, TelemetryTaxonomy.Results.AlreadyGranted, stopwatch.Elapsed));
                return true;
            }

            IReadOnlyCollection<string> scopesToPreserve = string.IsNullOrWhiteSpace(token)
                ? _credentialStore.GetAccountSession(userId)?.GrantedScopes ?? []
                : granted.ToArray();
            string[] requested = OAuthScopePolicy.BuildRequestedScopes(
                scopesToPreserve.Concat(requiredScopes).ToArray())
                .Append("offline_access").ToArray();
            long generation = Interlocked.Read(ref _sessionGeneration);
            GitHubTokenSession? session = await _devicePrompt.AuthorizeAsync(
                _appConfigService.Credential.ClientId, requested);
            if (session is null)
            {
                stopwatch.Stop();
                TrackEvent("auth.flow.completed", AuthProperties(
                    TelemetryTaxonomy.Sources.Scope, TelemetryTaxonomy.Results.Cancelled, stopwatch.Elapsed));
                return false;
            }

            ValidateDeviceSession(session);

            GitHubUser user = await _gitHubClientService.GetCurrentUserAsync(session.AccessToken);
            if (user.Id != userId)
            {
                throw new InvalidOperationException("GitHub approved a different account. Sign in with the current account to grant this permission.");
            }

            IReadOnlySet<string> updatedScopes = await _gitHubClientService.GetTokenScopesAsync(session.AccessToken);
            if (!OAuthScopePolicy.HasAll(updatedScopes, OAuthScopePolicy.BuildRequestedScopes(requiredScopes)))
            {
                throw new InvalidOperationException("GitHub did not grant the requested permission.");
            }

            session = session with { GrantedScopes = updatedScopes.ToArray() };
            if (generation != Interlocked.Read(ref _sessionGeneration) ||
                _accountService.GetUser() != userId)
            {
                throw new OperationCanceledException("The active account changed during authorization.");
            }
            await _refreshGate.WaitAsync();
            try
            {
                if (generation != Interlocked.Read(ref _sessionGeneration) ||
                    _accountService.GetUser() != userId)
                {
                    throw new OperationCanceledException("The active account changed during authorization.");
                }
                _credentialStore.SaveAccountSession(userId, session);
                _gitHubService.SetAccessToken(session.AccessToken);
                Interlocked.Increment(ref _sessionGeneration);
            }
            finally
            {
                _refreshGate.Release();
            }
            stopwatch.Stop();
            TrackEvent("auth.flow.completed", AuthProperties(
                TelemetryTaxonomy.Sources.Scope, TelemetryTaxonomy.Results.Success, stopwatch.Elapsed));
            return true;
        }
        catch (OperationCanceledException exception) when (IsTransportTimeout(exception))
        {
            stopwatch.Stop();
            TrackFlowCompletion(
                TelemetryTaxonomy.Sources.Scope,
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Network);
            TrackAuthError(TelemetryTaxonomy.Sources.Scope, "network", stopwatch.Elapsed);
            throw new HttpRequestException("GitHub permission request timed out.", exception);
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
        long generation = Interlocked.Increment(ref _sessionGeneration);
        string[] scopes = OAuthScopePolicy.BuildRequestedScopes(additionalScopes)
            .Append("offline_access").ToArray();
        GitHubTokenSession? session = await _devicePrompt.AuthorizeAsync(
            _appConfigService.Credential.ClientId, scopes);
        if (session is null)
        {
            throw new OperationCanceledException("GitHub sign-in was cancelled.");
        }

        ValidateDeviceSession(session);

        GitHubUser user = await _gitHubClientService.GetCurrentUserAsync(session.AccessToken);
        IReadOnlySet<string> grantedScopes = await _gitHubClientService.GetTokenScopesAsync(session.AccessToken);
        if (!OAuthScopePolicy.HasAll(grantedScopes, OAuthScopePolicy.BuildRequestedScopes()))
        {
            throw DeviceFlowException.For("insufficient_scope");
        }
        if (generation != Interlocked.Read(ref _sessionGeneration))
        {
            throw new OperationCanceledException("Sign-in was cancelled while GitHub authorized the account.");
        }
        session = session with { GrantedScopes = grantedScopes.ToArray() };
        _credentialStore.SaveAccountSession(user.Id, session);
        _accountService.SaveUser(user.Id);
        _accountWork.Activate(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AuthenticatedUser = user;
        Authenticated = true;
        _gitHubService.SetAccessToken(session.AccessToken);
        _initializeTask = Task.CompletedTask;
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
        long expectedUserId = AuthenticatedUser?.Id ?? _accountService.GetUser();
        long generation = Interlocked.Read(ref _sessionGeneration);
        string? token = await GetValidTokenAsync(expectedUserId);
        if (generation != Interlocked.Read(ref _sessionGeneration) ||
            _accountService.GetUser() != expectedUserId)
        {
            throw new OperationCanceledException("The active account changed during session refresh.");
        }
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
            if (Program.CurrentLaunchOptions.IsPublicPreviewOverride && GitHubClientService.IsPublicAccessToken(token))
            {
                GitHubUser previewUser = CreatePublicPreviewUser();
                _gitHubService.SetAccessToken(token);
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
            if (generation != Interlocked.Read(ref _sessionGeneration) ||
                _accountService.GetUser() != expectedUserId)
            {
                throw new OperationCanceledException("The active account changed during session refresh.");
            }
            if (expectedUserId > 0 && user.Id != expectedUserId)
            {
                throw new GitHubAuthenticationException("The stored GitHub session belongs to a different account.");
            }
            _gitHubService.SetAccessToken(_credentialStore.GetAccountSession(user.Id)?.AccessToken ?? token);
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
        catch (OperationCanceledException exception) when (IsTransportTimeout(exception))
        {
            if (!Authenticated)
            {
                RecoveryState = AuthSessionRecoveryState.Offline;
            }
            stopwatch.Stop();
            TrackAuthAction(
                TelemetryTaxonomy.Actions.RefreshUser,
                TelemetryTaxonomy.Results.Error,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Network);
            TrackAuthError("refresh", "network", stopwatch.Elapsed);
            return AuthenticatedUser;
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
        catch (GitHubAuthenticationException exception)
        {
            bool invalidated = HandleAuthenticationFailure(exception);
            if (invalidated)
            {
                RecoveryState = AuthSessionRecoveryState.Expired;
            }
            else if (!Authenticated)
            {
                RecoveryState = AuthSessionRecoveryState.ServiceUnavailable;
            }
            stopwatch.Stop();
            TrackAuthAction(
                TelemetryTaxonomy.Actions.RefreshUser,
                TelemetryTaxonomy.Results.AuthError,
                stopwatch.Elapsed,
                TelemetryTaxonomy.ErrorKinds.Authentication);
            TrackAuthError("refresh", "authentication", stopwatch.Elapsed);
            return invalidated ? null : AuthenticatedUser;
        }
        catch (GitHubApiException)
        {
            if (!Authenticated)
            {
                RecoveryState = AuthSessionRecoveryState.ServiceUnavailable;
            }
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
            if (!Authenticated)
            {
                RecoveryState = AuthSessionRecoveryState.Offline;
            }
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
            return null;
        }

        GitHubTokenSession? session = _credentialStore.GetAccountSession(userId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (session?.AccessTokenExpiresAt is { } expiry &&
            expiry <= now.AddMinutes(5) &&
            !string.IsNullOrWhiteSpace(session.RefreshToken) &&
            (session.RefreshTokenExpiresAt is not { } refreshExpiry || refreshExpiry > now))
        {
            QueueProactiveRefresh(userId);
        }

        return session?.AccessTokenExpiresAt is { } accessExpiry && accessExpiry <= now
            ? null
            : session?.AccessToken;
    }

    private void QueueProactiveRefresh(long userId)
    {
        lock (_proactiveRefreshGate)
        {
            if (_proactiveRefreshUserId == userId && DateTimeOffset.UtcNow < _nextProactiveRefreshAt)
            {
                return;
            }

            if (_proactiveRefreshTask is { IsCompleted: false } && _proactiveRefreshUserId == userId)
            {
                return;
            }

            _proactiveRefreshUserId = userId;
            _nextProactiveRefreshAt = DateTimeOffset.UtcNow.AddSeconds(30);
            _proactiveRefreshTask = RunProactiveRefreshAsync(userId);
        }
    }

    private async Task RunProactiveRefreshAsync(long userId)
    {
        try
        {
            await GetValidTokenAsync(userId).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A later foreground request retries; credentials are never written to diagnostics.
        }
    }

    public async Task<string?> GetValidTokenAsync(long userId, CancellationToken cancellationToken = default)
    {
        if (Program.CurrentLaunchOptions.IsPublicPreviewOverride)
        {
            return GitHubClientService.PublicAccessToken;
        }

        if (userId <= 0)
        {
            return null;
        }

        long requestGeneration = Interlocked.Read(ref _sessionGeneration);
        GitHubTokenSession? session = _credentialStore.GetAccountSession(userId);
        if (session is null || session.AccessTokenExpiresAt is not { } expiry ||
            expiry > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return session?.AccessToken;
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (requestGeneration != Interlocked.Read(ref _sessionGeneration))
            {
                throw new OperationCanceledException("The active session changed during token refresh.");
            }
            // Another request may have rotated this pair while we waited.
            session = _credentialStore.GetAccountSession(userId);
            if (session is null || session.AccessTokenExpiresAt is not { } currentExpiry ||
                currentExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return session?.AccessToken;
            }

            if (string.IsNullOrWhiteSpace(session.RefreshToken) ||
                session.RefreshTokenExpiresAt is { } refreshExpiry && refreshExpiry <= DateTimeOffset.UtcNow)
            {
                if (currentExpiry <= DateTimeOffset.UtcNow)
                {
                    RecoveryState = AuthSessionRecoveryState.Expired;
                }
                return currentExpiry > DateTimeOffset.UtcNow ? session.AccessToken : null;
            }

            try
            {
                GitHubTokenSession replacement = await _deviceClient.RefreshAsync(
                    _appConfigService.Credential.ClientId, session.RefreshToken, cancellationToken);
                if (requestGeneration != Interlocked.Read(ref _sessionGeneration) ||
                    _accountService.GetUser() != userId)
                {
                    throw new OperationCanceledException("The active session changed during token refresh.");
                }
                replacement = replacement with { GrantedScopes = session.GrantedScopes };
                _credentialStore.SaveAccountSession(userId, replacement);
                _gitHubService.SetAccessToken(replacement.AccessToken);
                RecoveryState = AuthSessionRecoveryState.None;
                return replacement.AccessToken;
            }
            catch (Exception exception) when (exception is HttpRequestException ||
                exception is OperationCanceledException canceled &&
                !cancellationToken.IsCancellationRequested && IsTransportTimeout(canceled))
            {
                if (requestGeneration != Interlocked.Read(ref _sessionGeneration) ||
                    _accountService.GetUser() != userId)
                {
                    throw new OperationCanceledException("The active session changed during token refresh.");
                }
                if (currentExpiry <= DateTimeOffset.UtcNow)
                {
                    RecoveryState = AuthSessionRecoveryState.Offline;
                }
                return currentExpiry > DateTimeOffset.UtcNow ? session.AccessToken : null;
            }
            catch (DeviceFlowException exception) when (exception.Code == "bad_refresh_token")
            {
                if (requestGeneration != Interlocked.Read(ref _sessionGeneration) ||
                    _accountService.GetUser() != userId)
                {
                    throw new OperationCanceledException("The active session changed during token refresh.");
                }
                if (requestGeneration == Interlocked.Read(ref _sessionGeneration) &&
                    _accountService.GetUser() == userId &&
                    _credentialStore.GetAccountSession(userId)?.RefreshToken == session.RefreshToken)
                {
                    _credentialStore.RemoveAccountToken(userId);
                    RecoveryState = AuthSessionRecoveryState.Expired;
                }
                return null;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
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
            Interlocked.Increment(ref _sessionGeneration);
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

    public bool HandleAuthenticationFailure(GitHubAuthenticationException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.RejectedTokenFingerprint is not { } rejectedFingerprint)
        {
            SignOut();
            return true;
        }

        // GitHub invalidates the old access token before a refresh is persisted.
        if (!_refreshGate.Wait(0))
        {
            return false;
        }

        try
        {
            long userId = _accountService.GetUser();
            string? currentToken = _credentialStore.GetAccountSession(userId)?.AccessToken;
            if (!string.Equals(
                    GitHubAuthenticationException.Fingerprint(currentToken),
                    rejectedFingerprint,
                    StringComparison.Ordinal))
            {
                return false;
            }

            SignOut();
            return true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RestoreSessionAsync()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        long userId = _accountService.GetUser();
        long generation = Interlocked.Read(ref _sessionGeneration);
        string? token;
        try
        {
            token = userId > 0 ? await GetValidTokenAsync(userId) : null;
        }
        catch (OperationCanceledException) when (
            generation != Interlocked.Read(ref _sessionGeneration) ||
            _accountService.GetUser() != userId)
        {
            return;
        }
        if (generation != Interlocked.Read(ref _sessionGeneration) ||
            _accountService.GetUser() != userId)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            if (userId > 0 && RecoveryState == AuthSessionRecoveryState.Offline &&
                _credentialStore.GetAccountSession(userId) is not null)
            {
                // An expired token can be refreshed after connectivity returns.
                Authenticated = false;
                AuthenticatedUser = null;
                _gitHubService.SetAccessToken(null);
                RecoveryState = AuthSessionRecoveryState.Offline;
                _initializeTask = Task.CompletedTask;
                return;
            }

            if (userId > 0)
            {
                _credentialStore.RemoveAccountToken(userId);
                _accountService.RemoveUser();
            }

            ClearAuthenticationState(clearPersistedSession: false);
            stopwatch.Stop();
            TrackEvent("auth.session.loaded", AuthProperties("startup", "no_session", stopwatch.Elapsed));
            return;
        }

        // Make the token available to startup data loaders immediately while we validate/refresh the session.
        _gitHubService.SetAccessToken(token);

        try
        {
            GitHubUser restoredUser = await _gitHubClientService.GetCurrentUserAsync(token);
            if (generation != Interlocked.Read(ref _sessionGeneration) ||
                _accountService.GetUser() != userId)
            {
                return;
            }
            if (restoredUser.Id != userId)
            {
                throw new GitHubAuthenticationException("The stored GitHub session belongs to a different account.");
            }

            AuthenticatedUser = restoredUser;
            _accountWork.Activate(restoredUser.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Authenticated = true;
            RecoveryState = AuthSessionRecoveryState.None;
            _initializeTask = Task.CompletedTask;
            stopwatch.Stop();
            TrackEvent("auth.session.loaded", AuthProperties("startup", "success", stopwatch.Elapsed));
        }
        catch (GitHubAuthenticationException exception)
        {
            if (generation != Interlocked.Read(ref _sessionGeneration) ||
                _accountService.GetUser() != userId)
            {
                return;
            }
            if (HandleAuthenticationFailure(exception))
            {
                RecoveryState = AuthSessionRecoveryState.Expired;
            }
            else
            {
                Authenticated = false;
                AuthenticatedUser = null;
                _initializeTask = null;
                RecoveryState = AuthSessionRecoveryState.ServiceUnavailable;
            }
            stopwatch.Stop();
            TrackAuthError("startup", "authentication", stopwatch.Elapsed);
        }
        catch (GitHubApiException)
        {
            if (generation != Interlocked.Read(ref _sessionGeneration) ||
                _accountService.GetUser() != userId)
            {
                return;
            }
            Authenticated = false;
            AuthenticatedUser = null;
            _gitHubService.SetAccessToken(token);
            _initializeTask = null;
            RecoveryState = AuthSessionRecoveryState.ServiceUnavailable;
            stopwatch.Stop();
            TrackAuthError("startup", "api", stopwatch.Elapsed);
        }
        catch (Exception exception) when (exception is HttpRequestException ||
            exception is OperationCanceledException canceled && IsTransportTimeout(canceled))
        {
            if (generation != Interlocked.Read(ref _sessionGeneration) ||
                _accountService.GetUser() != userId)
            {
                return;
            }
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

    private void ClearAuthenticationState(bool clearPersistedSession)
    {
        if (clearPersistedSession)
        {
            long userId = _accountService.GetUser();
            _credentialStore.RemoveAccountToken(userId);
            _accountService.RemoveUser();
        }

        Authenticated = false;
        AuthenticatedUser = null;
        _gitHubService.SetAccessToken(null);
        _initializeTask = Task.CompletedTask;
    }

    private void RestoreSavedSessionAfterFailedSignIn(
        AuthSessionRecoveryState priorState,
        long userId,
        AuthSessionRecoveryState fallbackState)
    {
        if (userId > 0 && _accountService.GetUser() == userId &&
            _credentialStore.GetAccountSession(userId) is not null)
        {
            _initializeTask = null;
            RecoveryState = priorState is AuthSessionRecoveryState.Offline or AuthSessionRecoveryState.ServiceUnavailable
                ? priorState
                : AuthSessionRecoveryState.ServiceUnavailable;
            return;
        }

        RecoveryState = fallbackState;
    }

    private static bool IsTransportTimeout(OperationCanceledException exception) =>
        exception.InnerException is TimeoutException;

    private static void ValidateDeviceSession(GitHubTokenSession session)
    {
        if (string.IsNullOrWhiteSpace(session.AccessToken) ||
            string.IsNullOrWhiteSpace(session.RefreshToken) ||
            session.AccessTokenExpiresAt is null || session.RefreshTokenExpiresAt is null)
        {
            throw new InvalidOperationException("GitHub did not return an expiring token and refresh token.");
        }
    }
}
