using JitHub.Core.Models.Activation;
using JitHub.Core.Models.Authentication;
using JitHub.Core.Models.GitHub.Users;
using JitHub.Core.Services.Accounts;
using JitHub.Core.Services.Authentication;
using JitHub.Core.Services.Configuration;
using JitHub.Core.Services.GitHub;
using JitHub.Core.Services.GitHub.Transport;
using JitHub.Core.Tests.TestSupport;
using NSubstitute;
using Xunit;

namespace JitHub.Core.Tests.Services.Authentication;

public sealed class AuthServiceActivityTests
{
    private const long UserId = 42;
    private const string ClientId = "activity-test-client";

    [Fact]
    public async Task Restore_ExpiredTokenReportsRestoringUntilRefreshFinishes()
    {
        Fixture fixture = new();
        fixture.Account.GetUser().Returns(UserId);
        GitHubTokenSession expired = NewSession("old-access", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-1));
        GitHubTokenSession refreshed = NewSession("new-access", "new-refresh", DateTimeOffset.UtcNow.AddHours(8));
        fixture.Credentials.GetAccountSession(UserId).Returns(expired);
        TaskCompletionSource<GitHubTokenSession> refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.DeviceClient.RefreshAsync(ClientId, "old-refresh", Arg.Any<CancellationToken>()).Returns(refresh.Task);
        fixture.GitHub.GetCurrentUserAsync("new-access", Arg.Any<CancellationToken>())
            .Returns(new GitHubUser { Id = UserId, Login = "octocat" });

        Task restore = fixture.Service.InitializeAsync();

        Assert.Equal(AuthSessionActivity.RestoringSession, fixture.Service.Activity);
        refresh.SetResult(refreshed);
        await restore;
        Assert.Equal(AuthSessionActivity.Idle, fixture.Service.Activity);
        Assert.True(fixture.Service.Authenticated);
    }

    [Fact]
    public async Task Restore_WithoutSavedAccountStaysIdle()
    {
        Fixture fixture = new();
        int changes = 0;
        fixture.Service.ActivityChanged += (_, _) => changes++;

        await fixture.Service.InitializeAsync();

        Assert.Equal(AuthSessionActivity.Idle, fixture.Service.Activity);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task SignIn_ReportsCompletingOnlyAfterDeviceApproval()
    {
        Fixture fixture = new();
        GitHubTokenSession session = NewSession("access", "refresh", DateTimeOffset.UtcNow.AddHours(8));
        AuthSessionActivity activityDuringPrompt = AuthSessionActivity.CompletingSignIn;
        AuthSessionActivity activityDuringAccountLookup = AuthSessionActivity.Idle;
        fixture.Prompt.AuthorizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                activityDuringPrompt = fixture.Service.Activity;
                return Task.FromResult<GitHubTokenSession?>(session);
            });
        fixture.GitHub.GetCurrentUserAsync("access", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                activityDuringAccountLookup = fixture.Service.Activity;
                return Task.FromResult(new GitHubUser { Id = UserId, Login = "octocat" });
            });
        fixture.GitHub.GetTokenScopesAsync("access", Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(["user", "repo", "notifications"]));

        await fixture.Service.Authenticate();

        Assert.Equal(AuthSessionActivity.Idle, activityDuringPrompt);
        Assert.Equal(AuthSessionActivity.CompletingSignIn, activityDuringAccountLookup);
        Assert.Equal(AuthSessionActivity.Idle, fixture.Service.Activity);
    }

    [Fact]
    public async Task SignIn_FailureAfterApprovalReturnsToIdle()
    {
        Fixture fixture = new();
        GitHubTokenSession session = NewSession("access", "refresh", DateTimeOffset.UtcNow.AddHours(8));
        fixture.Prompt.AuthorizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(session);
        fixture.GitHub.GetCurrentUserAsync("access", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<GitHubUser>(new HttpRequestException("offline")));

        await Assert.ThrowsAsync<HttpRequestException>(fixture.Service.Authenticate);

        Assert.Equal(AuthSessionActivity.Idle, fixture.Service.Activity);
        Assert.False(fixture.Service.Authenticated);
    }

    private static GitHubTokenSession NewSession(string access, string refresh, DateTimeOffset expiresAt) =>
        new(access, refresh, expiresAt, DateTimeOffset.UtcNow.AddMonths(6));

    private sealed class Fixture
    {
        public Fixture()
        {
            IAppConfig appConfig = Substitute.For<IAppConfig>();
            appConfig.Credential.Returns(new Credential { ClientId = ClientId });
            Service = new AuthService(
                appConfig,
                Account,
                GitHub,
                Substitute.For<IGitHubAccessTokenSink>(),
                Substitute.For<IUnauthorizedNavigator>(),
                Credentials,
                new AccountWorkQuiescence(),
                new RecordingTelemetryService(),
                Prompt,
                DeviceClient,
                new LaunchOptions());
        }

        public AuthService Service { get; }
        public IAccountService Account { get; } = Substitute.For<IAccountService>();
        public IGitHubClientService GitHub { get; } = Substitute.For<IGitHubClientService>();
        public IAuthCredentialStore Credentials { get; } = Substitute.For<IAuthCredentialStore>();
        public IDeviceAuthorizationPrompt Prompt { get; } = Substitute.For<IDeviceAuthorizationPrompt>();
        public IGitHubDeviceFlowClient DeviceClient { get; } = Substitute.For<IGitHubDeviceFlowClient>();
    }
}
