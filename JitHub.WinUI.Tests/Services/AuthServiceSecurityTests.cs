using JitHub.Models;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.Tests.TestDoubles;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class AuthServiceSecurityTests
{
    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task DeviceSignIn_ValidatesIdentityBeforeStoringExpiringSession()
    {
        TestContext context = CreateContext();
        GitHubTokenSession pair = NewSession("access-token", "refresh-token");
        context.Prompt.AuthorizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(pair);
        context.GitHubClient.GetCurrentUserAsync(pair.AccessToken, Arg.Any<CancellationToken>())
            .Returns(new GitHubUser { Id = 42, Login = "octocat" });
        context.GitHubClient.GetTokenScopesAsync(pair.AccessToken, Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(["user", "repo", "notifications"]));

        await context.Service.Authenticate();

        Assert.True(context.Service.Authenticated);
        Assert.Equal(42, context.Account.UserId);
        GitHubTokenSession? saved = context.CredentialStore.GetAccountSession(42);
        Assert.NotNull(saved);
        Assert.Equal(pair.AccessToken, saved.AccessToken);
        Assert.Equal(pair.RefreshToken, saved.RefreshToken);
        Assert.Equal(pair.AccessTokenExpiresAt, saved.AccessTokenExpiresAt);
        Assert.Equal(pair.RefreshTokenExpiresAt, saved.RefreshTokenExpiresAt);
        Assert.Equal(pair.AccessToken, context.GitHubService.AccessToken);
        await context.Prompt.Received(1).AuthorizeAsync(
            "security-test-client",
            Arg.Is<IReadOnlyCollection<string>>(scopes =>
                scopes.Contains("user") && scopes.Contains("repo") &&
                scopes.Contains("notifications") && scopes.Contains("offline_access")),
            Arg.Any<CancellationToken>());
        Assert.DoesNotContain(context.Telemetry.Events.SelectMany(entry => entry.Properties.Values),
            value => value is "access-token" or "refresh-token" or "octocat" or "42");
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task DeviceSignIn_CancellationDoesNotReplaceExistingToken()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountToken(42, "existing-token");
        context.Prompt.AuthorizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns((GitHubTokenSession?)null);

        await Assert.ThrowsAsync<OperationCanceledException>(() => context.Service.Authenticate());

        Assert.Equal("existing-token", context.CredentialStore.GetAccountToken(42));
        Assert.Equal(42, context.Account.UserId);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task CancelledManualSignIn_RestoresExistingSessionBeforeInitialLoad()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountToken(42, "existing-token");
        context.Prompt.AuthorizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns((GitHubTokenSession?)null);
        context.GitHubClient.GetCurrentUserAsync("existing-token", Arg.Any<CancellationToken>())
            .Returns(new GitHubUser { Id = 42, Login = "octocat" });

        await Assert.ThrowsAsync<OperationCanceledException>(() => context.Service.Authenticate());
        Assert.Equal(AuthSessionRecoveryState.ServiceUnavailable, context.Service.RecoveryState);

        await context.Service.InitializeAsync();

        Assert.True(context.Service.Authenticated);
        Assert.Equal(AuthSessionRecoveryState.None, context.Service.RecoveryState);
        Assert.Equal("existing-token", context.CredentialStore.GetAccountToken(42));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task DeviceSignIn_RejectsMissingBaselineScopeWithoutReplacingSession()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountToken(42, "existing-token");
        context.Prompt.AuthorizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(NewSession("limited-access", "limited-refresh"));
        context.GitHubClient.GetCurrentUserAsync("limited-access", Arg.Any<CancellationToken>())
            .Returns(new GitHubUser { Id = 99, Login = "limited" });
        context.GitHubClient.GetTokenScopesAsync("limited-access", Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(["user", "repo"]));

        DeviceFlowException error = await Assert.ThrowsAsync<DeviceFlowException>(() => context.Service.Authenticate());
        Assert.Equal("insufficient_scope", error.Code);

        Assert.Equal("existing-token", context.CredentialStore.GetAccountToken(42));
        Assert.Null(context.CredentialStore.GetAccountSession(99));
        Assert.Equal(42, context.Account.UserId);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task DeviceSignIn_HttpTimeoutIsNotReportedAsUserCancellation()
    {
        TestContext context = CreateContext();
        context.Prompt.AuthorizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<GitHubTokenSession?>(
                new TaskCanceledException("timeout", new TimeoutException())));

        await Assert.ThrowsAsync<HttpRequestException>(() => context.Service.Authenticate());

        Assert.NotEqual(AuthSessionRecoveryState.Cancelled, context.Service.RecoveryState);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task EnsureScopes_UsesExistingScopesAndRejectsDifferentAccount()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountToken(42, "existing-token");
        context.GitHubClient.GetTokenScopesAsync("existing-token", Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(["repo", "user", "notifications", "gist"]));
        context.Prompt.AuthorizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(NewSession("other-account-token", "refresh-token"));
        context.GitHubClient.GetCurrentUserAsync("other-account-token", Arg.Any<CancellationToken>())
            .Returns(new GitHubUser { Id = 99, Login = "other" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.EnsureScopesAsync("delete_repo"));

        Assert.Equal("existing-token", context.CredentialStore.GetAccountToken(42));
        await context.Prompt.Received(1).AuthorizeAsync(
            "security-test-client",
            Arg.Is<IReadOnlyCollection<string>>(scopes =>
                scopes.Contains("gist") && scopes.Contains("delete_repo") && scopes.Contains("offline_access")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Refresh_RotatesOnceForConcurrentCallers()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42, NewSession("old-access", "old-refresh", DateTimeOffset.UtcNow.AddSeconds(30)));
        GitHubTokenSession replacement = NewSession("new-access", "new-refresh");
        context.DeviceClient.RefreshAsync("security-test-client", "old-refresh", Arg.Any<CancellationToken>())
            .Returns(replacement);

        string?[] tokens = await Task.WhenAll(
            context.Service.GetValidTokenAsync(42),
            context.Service.GetValidTokenAsync(42));

        Assert.All(tokens, token => Assert.Equal("new-access", token));
        GitHubTokenSession? saved = context.CredentialStore.GetAccountSession(42);
        Assert.NotNull(saved);
        Assert.Equal(replacement.AccessToken, saved.AccessToken);
        Assert.Equal(replacement.RefreshToken, saved.RefreshToken);
        Assert.Equal(replacement.AccessTokenExpiresAt, saved.AccessTokenExpiresAt);
        Assert.Equal(replacement.RefreshTokenExpiresAt, saved.RefreshTokenExpiresAt);
        await context.DeviceClient.Received(1).RefreshAsync(
            "security-test-client", "old-refresh", Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Refresh_OfflinePreservesPairForLaterRecovery()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42,
            NewSession("old-access", "old-refresh", DateTimeOffset.UtcNow.AddSeconds(-1)));
        context.DeviceClient.RefreshAsync("security-test-client", "old-refresh", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<GitHubTokenSession>(new HttpRequestException("offline")));

        Assert.Null(await context.Service.GetValidTokenAsync(42));
        Assert.Equal(AuthSessionRecoveryState.Offline, context.Service.RecoveryState);
        Assert.Equal("old-refresh", context.CredentialStore.GetAccountSession(42)?.RefreshToken);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Refresh_TimeoutPreservesPairAndAllowsRetry()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42,
            NewSession("old-access", "old-refresh", DateTimeOffset.UtcNow.AddSeconds(-1)));
        bool timedOut = true;
        context.DeviceClient.RefreshAsync("security-test-client", "old-refresh", Arg.Any<CancellationToken>())
            .Returns(_ => timedOut
                ? Task.FromException<GitHubTokenSession>(new TaskCanceledException("timeout", new TimeoutException()))
                : Task.FromResult(NewSession("new-access", "new-refresh")));
        context.GitHubClient.GetCurrentUserAsync("new-access", Arg.Any<CancellationToken>())
            .Returns(new GitHubUser { Id = 42, Login = "octocat" });

        Assert.Null(await context.Service.RefreshAuthenticatedUserAsync());
        Assert.Equal(AuthSessionRecoveryState.Offline, context.Service.RecoveryState);
        Assert.Equal("old-refresh", context.CredentialStore.GetAccountSession(42)?.RefreshToken);

        timedOut = false;
        Assert.Equal(42, (await context.Service.RefreshAuthenticatedUserAsync())?.Id);
        Assert.Equal("new-refresh", context.CredentialStore.GetAccountSession(42)?.RefreshToken);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task AccountLookup_TimeoutKeepsSavedSessionForRetry()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42, NewSession("access", "refresh"));
        bool timedOut = true;
        context.GitHubClient.GetCurrentUserAsync("access", Arg.Any<CancellationToken>())
            .Returns(_ => timedOut
                ? Task.FromException<GitHubUser>(new TaskCanceledException("timeout", new TimeoutException()))
                : Task.FromResult(new GitHubUser { Id = 42, Login = "octocat" }));

        Assert.Null(await context.Service.RefreshAuthenticatedUserAsync());
        Assert.Equal(AuthSessionRecoveryState.Offline, context.Service.RecoveryState);
        Assert.Equal("refresh", context.CredentialStore.GetAccountSession(42)?.RefreshToken);

        timedOut = false;
        Assert.Equal(42, (await context.Service.RefreshAuthenticatedUserAsync())?.Id);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Refresh_ExplicitCancellationIsNotTreatedAsTimeout()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42,
            NewSession("old-access", "old-refresh", DateTimeOffset.UtcNow.AddSeconds(-1)));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Service.GetValidTokenAsync(42, cancellation.Token));

        Assert.Equal(AuthSessionRecoveryState.None, context.Service.RecoveryState);
        Assert.Equal("old-refresh", context.CredentialStore.GetAccountSession(42)?.RefreshToken);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task CancelledManualSignIn_ResumesPendingSavedSessionRecovery()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42,
            NewSession("old-access", "old-refresh", DateTimeOffset.UtcNow.AddSeconds(-1)));
        bool offline = true;
        context.DeviceClient.RefreshAsync("security-test-client", "old-refresh", Arg.Any<CancellationToken>())
            .Returns(_ => offline
                ? Task.FromException<GitHubTokenSession>(new HttpRequestException("offline"))
                : Task.FromResult(NewSession("new-access", "new-refresh")));
        context.Prompt.AuthorizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns((GitHubTokenSession?)null);
        context.GitHubClient.GetCurrentUserAsync("new-access", Arg.Any<CancellationToken>())
            .Returns(new GitHubUser { Id = 42, Login = "octocat" });

        Assert.Null(await context.Service.RefreshAuthenticatedUserAsync());
        Assert.Equal(AuthSessionRecoveryState.Offline, context.Service.RecoveryState);
        await Assert.ThrowsAsync<OperationCanceledException>(() => context.Service.Authenticate());
        Assert.Equal(AuthSessionRecoveryState.Offline, context.Service.RecoveryState);
        Assert.Equal("old-refresh", context.CredentialStore.GetAccountSession(42)?.RefreshToken);

        offline = false;
        await context.Service.InitializeAsync();
        Assert.True(context.Service.Authenticated);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task RefreshAuthenticatedUser_DoesNotOverwriteAConcurrentTokenRotation()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42, NewSession("old-access", "old-refresh"));
        TaskCompletionSource<GitHubUser> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.GitHubClient.GetCurrentUserAsync("old-access", Arg.Any<CancellationToken>())
            .Returns(_ => response.Task);

        Task<GitHubUser?> pending = context.Service.RefreshAuthenticatedUserAsync();
        GitHubTokenSession rotated = NewSession("new-access", "new-refresh");
        context.CredentialStore.SaveAccountSession(42, rotated);
        response.SetResult(new GitHubUser { Id = 42, Login = "octocat" });

        Assert.Equal(42, (await pending)?.Id);
        GitHubTokenSession? saved = context.CredentialStore.GetAccountSession(42);
        Assert.Equal(rotated.AccessToken, saved?.AccessToken);
        Assert.Equal(rotated.RefreshToken, saved?.RefreshToken);
        Assert.Equal(rotated.AccessToken, context.GitHubService.AccessToken);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task RefreshAuthenticatedUser_DoesNotRestoreAUserAfterSignOut()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42, NewSession("old-access", "old-refresh"));
        TaskCompletionSource<GitHubUser> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.GitHubClient.GetCurrentUserAsync("old-access", Arg.Any<CancellationToken>())
            .Returns(_ => response.Task);

        Task<GitHubUser?> pending = context.Service.RefreshAuthenticatedUserAsync();
        context.Service.SignOut();
        response.SetResult(new GitHubUser { Id = 42, Login = "octocat" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(context.Service.Authenticated);
        Assert.Null(context.CredentialStore.GetAccountSession(42));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task RefreshAuthenticatedUser_DoesNotClearANewSignInAfterOldRefreshCompletes()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42,
            NewSession("old-access", "old-refresh", DateTimeOffset.UtcNow.AddSeconds(30)));
        TaskCompletionSource<GitHubTokenSession> refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.DeviceClient.RefreshAsync("security-test-client", "old-refresh", Arg.Any<CancellationToken>())
            .Returns(_ => refresh.Task);

        Task<GitHubUser?> pending = context.Service.RefreshAuthenticatedUserAsync();
        await SignInAsNewAccount(context);
        refresh.SetResult(NewSession("old-rotated-access", "old-rotated-refresh"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        AssertNewAccountPreserved(context);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Restore_DoesNotOverwriteANewSignInAfterOldUserLookupCompletes()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountToken(42, "old-access");
        TaskCompletionSource<GitHubUser> oldUser = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.GitHubClient.GetCurrentUserAsync("old-access", Arg.Any<CancellationToken>())
            .Returns(_ => oldUser.Task);

        Task restore = context.Service.InitializeAsync();
        await SignInAsNewAccount(context);
        oldUser.SetResult(new GitHubUser { Id = 42, Login = "old-account" });

        await restore;
        AssertNewAccountPreserved(context);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Restore_DoesNotFailAfterNewSignInCancelsOldRefresh()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42,
            NewSession("old-access", "old-refresh", DateTimeOffset.UtcNow.AddSeconds(30)));
        TaskCompletionSource<GitHubTokenSession> refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.DeviceClient.RefreshAsync("security-test-client", "old-refresh", Arg.Any<CancellationToken>())
            .Returns(_ => refresh.Task);

        Task restore = context.Service.InitializeAsync();
        await SignInAsNewAccount(context);
        refresh.SetResult(NewSession("old-rotated-access", "old-rotated-refresh"));

        await restore;
        AssertNewAccountPreserved(context);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Restore_OfflineExpiredSessionCanRetryWithoutAnotherSignIn()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42,
            NewSession("old-access", "old-refresh", DateTimeOffset.UtcNow.AddSeconds(-1)));
        int attempts = 0;
        context.DeviceClient.RefreshAsync("security-test-client", "old-refresh", Arg.Any<CancellationToken>())
            .Returns(_ => ++attempts == 1
                ? Task.FromException<GitHubTokenSession>(new HttpRequestException("offline"))
                : Task.FromResult(NewSession("new-access", "new-refresh")));
        context.GitHubClient.GetCurrentUserAsync("new-access", Arg.Any<CancellationToken>())
            .Returns(new GitHubUser { Id = 42, Login = "octocat" });

        await context.Service.InitializeAsync();
        Assert.Equal(AuthSessionRecoveryState.Offline, context.Service.RecoveryState);
        Assert.Equal("old-refresh", context.CredentialStore.GetAccountSession(42)?.RefreshToken);

        Assert.Equal(42, (await context.Service.RefreshAuthenticatedUserAsync())?.Id);
        Assert.True(context.Service.Authenticated);
        Assert.Equal(AuthSessionRecoveryState.None, context.Service.RecoveryState);
        Assert.Equal("new-refresh", context.CredentialStore.GetAccountSession(42)?.RefreshToken);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Retry_WhenAccountLookupIsOffline_KeepsRecoveryPending()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42,
            NewSession("old-access", "old-refresh", DateTimeOffset.UtcNow.AddSeconds(-1)));
        context.DeviceClient.RefreshAsync("security-test-client", "old-refresh", Arg.Any<CancellationToken>())
            .Returns(NewSession("new-access", "new-refresh"));
        context.GitHubClient.GetCurrentUserAsync("new-access", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<GitHubUser>(new HttpRequestException("offline")));

        Assert.Null(await context.Service.RefreshAuthenticatedUserAsync());

        Assert.Equal(AuthSessionRecoveryState.Offline, context.Service.RecoveryState);
        Assert.Equal("new-refresh", context.CredentialStore.GetAccountSession(42)?.RefreshToken);
        Assert.Equal(42, context.Account.UserId);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Restore_ExpiredRefreshTokenRequiresNewSignIn()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42,
            NewSession("old-access", "old-refresh", DateTimeOffset.UtcNow.AddSeconds(-1)) with
            { RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });

        await context.Service.InitializeAsync();

        Assert.Equal(AuthSessionRecoveryState.Expired, context.Service.RecoveryState);
        Assert.Null(context.CredentialStore.GetAccountSession(42));
        Assert.Equal(0, context.Account.UserId);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Refresh_DoesNotRestoreCredentialAfterSignOut()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42,
            NewSession("old-access", "old-refresh", DateTimeOffset.UtcNow.AddSeconds(30)));
        TaskCompletionSource<GitHubTokenSession> refresh = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.DeviceClient.RefreshAsync("security-test-client", "old-refresh", Arg.Any<CancellationToken>())
            .Returns(_ => refresh.Task);

        Task<string?> pending = context.Service.GetValidTokenAsync(42);
        context.Service.SignOut();
        refresh.SetResult(NewSession("new-access", "new-refresh"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(context.CredentialStore.GetAccountSession(42));
        Assert.Null(context.GitHubService.AccessToken);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void AuthenticationFailure_FromRotatedTokenPreservesCurrentSession()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42, NewSession("new-access", "new-refresh"));
        context.Service.Authenticated = true;
        context.Service.AuthenticatedUser = new GitHubUser { Id = 42, Login = "octocat" };
        GitHubAuthenticationException failure = new("Unauthorized", "old-access");

        Assert.False(context.Service.HandleAuthenticationFailure(failure));

        Assert.True(context.Service.Authenticated);
        Assert.Equal(42, context.Account.UserId);
        Assert.Equal("new-refresh", context.CredentialStore.GetAccountSession(42)?.RefreshToken);
        Assert.DoesNotContain("old-access", failure.ToString());
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task AuthenticationFailure_DuringTokenRotationPreservesSession()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42,
            NewSession("old-access", "old-refresh", DateTimeOffset.UtcNow.AddSeconds(30)));
        TaskCompletionSource<GitHubTokenSession> refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.DeviceClient.RefreshAsync("security-test-client", "old-refresh", Arg.Any<CancellationToken>())
            .Returns(_ => refresh.Task);

        Task<string?> pending = context.Service.GetValidTokenAsync(42);
        Assert.False(context.Service.HandleAuthenticationFailure(
            new GitHubAuthenticationException("Unauthorized", "old-access")));
        refresh.SetResult(NewSession("new-access", "new-refresh"));

        Assert.Equal("new-access", await pending);
        Assert.Equal(42, context.Account.UserId);
        Assert.Equal("new-refresh", context.CredentialStore.GetAccountSession(42)?.RefreshToken);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void AuthenticationFailure_FromCurrentTokenClearsSession()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42, NewSession("current-access", "current-refresh"));
        context.Service.Authenticated = true;

        Assert.True(context.Service.HandleAuthenticationFailure(
            new GitHubAuthenticationException("Unauthorized", "current-access")));

        Assert.False(context.Service.Authenticated);
        Assert.Equal(0, context.Account.UserId);
        Assert.Null(context.CredentialStore.GetAccountSession(42));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task PermissionRequest_DoesNotRestoreRevokedScopeAfterRefresh()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42,
            NewSession("old-access", "old-refresh", DateTimeOffset.UtcNow.AddSeconds(30)) with
            { GrantedScopes = ["gist"] });
        context.DeviceClient.RefreshAsync("security-test-client", "old-refresh", Arg.Any<CancellationToken>())
            .Returns(NewSession("new-access", "new-refresh"));
        context.GitHubClient.GetTokenScopesAsync("new-access", Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(["repo", "user", "notifications"]));
        context.Prompt.AuthorizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(NewSession("granted-access", "granted-refresh"));
        context.GitHubClient.GetCurrentUserAsync("granted-access", Arg.Any<CancellationToken>())
            .Returns(new GitHubUser { Id = 42, Login = "octocat" });
        context.GitHubClient.GetTokenScopesAsync("granted-access", Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(["repo", "user", "notifications", "delete_repo"]));

        Assert.True(await context.Service.EnsureScopesAsync("delete_repo"));
        await context.Prompt.Received(1).AuthorizeAsync(
            "security-test-client",
            Arg.Is<IReadOnlyCollection<string>>(scopes =>
                !scopes.Contains("gist") && scopes.Contains("delete_repo") && scopes.Contains("offline_access")),
            Arg.Any<CancellationToken>());
        Assert.DoesNotContain("gist", context.CredentialStore.GetAccountSession(42)?.GrantedScopes ?? []);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task PermissionRequest_DoesNotTreatRevokedStoredScopeAsGranted()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountSession(42,
            NewSession("old-access", "old-refresh") with { GrantedScopes = ["gist"] });
        context.GitHubClient.GetTokenScopesAsync("old-access", Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(["repo", "user"]));
        context.Prompt.AuthorizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(NewSession("granted-access", "granted-refresh"));
        context.GitHubClient.GetCurrentUserAsync("granted-access", Arg.Any<CancellationToken>())
            .Returns(new GitHubUser { Id = 42, Login = "octocat" });
        context.GitHubClient.GetTokenScopesAsync("granted-access", Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(["repo", "user", "notifications", "gist"]));

        Assert.True(await context.Service.EnsureScopesAsync("gist"));

        await context.Prompt.Received(1).AuthorizeAsync(
            "security-test-client",
            Arg.Is<IReadOnlyCollection<string>>(scopes => scopes.Contains("gist")),
            Arg.Any<CancellationToken>());
        Assert.Equal("granted-access", context.CredentialStore.GetAccountSession(42)?.AccessToken);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task LegacyToken_RemainsUsableWithoutRefresh()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountToken(42, "existing-token");

        Assert.Equal("existing-token", await context.Service.GetValidTokenAsync(42));
        Assert.Equal("existing-token", context.Service.GetToken(42));
        await context.DeviceClient.DidNotReceiveWithAnyArgs()
            .RefreshAsync(default!, default!, default);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Restore_RejectsTokenForDifferentStoredAccount()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SaveAccountToken(42, "other-account-token");
        context.GitHubClient.GetCurrentUserAsync("other-account-token", Arg.Any<CancellationToken>())
            .Returns(new GitHubUser { Id = 99, Login = "other" });

        await context.Service.InitializeAsync();

        Assert.False(context.Service.Authenticated);
        Assert.Null(context.CredentialStore.GetAccountSession(42));
        Assert.Null(context.GitHubService.AccessToken);
        Assert.Equal(AuthSessionRecoveryState.Expired, context.Service.RecoveryState);
    }

    private static GitHubTokenSession NewSession(
        string access,
        string refresh,
        DateTimeOffset? expiresAt = null) => new(
            access,
            refresh,
        expiresAt ?? DateTimeOffset.UtcNow.AddHours(8),
        DateTimeOffset.UtcNow.AddMonths(6));

    private static async Task SignInAsNewAccount(TestContext context)
    {
        context.Prompt.AuthorizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(NewSession("new-access", "new-refresh"));
        context.GitHubClient.GetCurrentUserAsync("new-access", Arg.Any<CancellationToken>())
            .Returns(new GitHubUser { Id = 99, Login = "new-account" });
        context.GitHubClient.GetTokenScopesAsync("new-access", Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(["repo", "user", "notifications"]));
        await context.Service.Authenticate();
    }

    private static void AssertNewAccountPreserved(TestContext context)
    {
        Assert.True(context.Service.Authenticated);
        Assert.Equal(99, context.Service.AuthenticatedUser?.Id);
        Assert.Equal(99, context.Account.UserId);
        Assert.Equal("new-access", context.CredentialStore.GetAccountSession(99)?.AccessToken);
        Assert.Equal("new-refresh", context.CredentialStore.GetAccountSession(99)?.RefreshToken);
        Assert.Equal("new-access", context.GitHubService.AccessToken);
    }

    private static TestContext CreateContext()
    {
        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        IDeviceAuthorizationPrompt prompt = Substitute.For<IDeviceAuthorizationPrompt>();
        IGitHubDeviceFlowClient deviceClient = Substitute.For<IGitHubDeviceFlowClient>();
        AuthCredentialStore credentials = new(new MemoryCredentialVaultBackend(), new TestAppConfig());
        TestAccountService account = new();
        RecordingGitHubService githubService = new();
        RecordingTelemetryService telemetry = new();
        AuthService service = new(
            new TestAppConfig(), account, client, githubService,
            new NavigationService(), credentials,
            new AccountWorkQuiescence(), telemetry, prompt, deviceClient);
        return new(service, client, githubService, credentials, account, telemetry, prompt, deviceClient);
    }

    private sealed record TestContext(
        AuthService Service,
        IGitHubClientService GitHubClient,
        RecordingGitHubService GitHubService,
        AuthCredentialStore CredentialStore,
        TestAccountService Account,
        RecordingTelemetryService Telemetry,
        IDeviceAuthorizationPrompt Prompt,
        IGitHubDeviceFlowClient DeviceClient);

    private sealed class TestAppConfig : IAppConfig
    {
        public Credential Credential { get; } = new() { ClientId = "security-test-client" };
    }

    private sealed class TestAccountService : IAccountService
    {
        public long UserId { get; private set; }
        public void RemoveUser() => UserId = 0;
        public void SaveUser(long userId) => UserId = userId;
        public long GetUser() => UserId;
    }

    private sealed class RecordingGitHubService : IGitHubService
    {
        public string? AccessToken { get; private set; }
        public void SetAccessToken(string? token) => AccessToken = token;
    }

    private sealed class MemoryCredentialVaultBackend : ICredentialVaultBackend
    {
        private readonly Dictionary<(string Resource, string UserName), string> _values = [];
        public string? Retrieve(string resource, string userName) =>
            _values.TryGetValue((resource, userName), out string? value) ? value : null;
        public void Store(string resource, string userName, string secret) => _values[(resource, userName)] = secret;
        public void Remove(string resource, string userName) => _values.Remove((resource, userName));
    }
}
