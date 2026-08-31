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
    public void GetToken_PositiveAccountNeverFallsBackToPendingToken()
    {
        TestContext context = CreateContext();
        context.Account.SaveUser(42);
        context.CredentialStore.SavePendingToken("pending-token-for-an-unresolved-account");

        Assert.Null(context.Service.GetToken(42));
        Assert.False(context.Service.CheckAuth(42));
        Assert.Equal("pending-token-for-an-unresolved-account", context.Service.GetToken(0));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Initialize_StaleAccountWithoutCredentialIsClearedWithoutUsingPendingToken()
    {
        TestContext context = CreateContext();
        await context.Service.Authenticate();
        context.Account.SaveUser(42);
        context.CredentialStore.SavePendingToken("pending-token-for-an-unresolved-account");

        await context.Service.InitializeAsync();

        Assert.Equal(0, context.Account.UserId);
        Assert.False(context.Service.Authenticated);
        Assert.Null(context.GitHubService.AccessToken);
        Assert.NotNull(context.CredentialStore.GetPendingState());
        Assert.NotNull(context.CredentialStore.GetPendingVerifier());
        Assert.Equal("pending-token-for-an-unresolved-account", context.CredentialStore.GetPendingToken());
        await context.GitHubClient.DidNotReceiveWithAnyArgs()
            .GetCurrentUserAsync(default!, default);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Authorize_StaleAccountIsRemovedBeforePendingTokenIdentityResolution()
    {
        TestContext context = CreateContext();
        await context.Service.Authenticate();
        string expectedState = Assert.IsType<string>(context.CredentialStore.GetPendingState());
        context.Account.SaveUser(42);
        context.HandoffClient.Token = "new-account-token";
        context.GitHubClient
            .GetCurrentUserAsync("new-account-token", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Assert.Equal(0, context.Account.UserId);
                Assert.Null(context.Service.GetToken(42));
                return new GitHubUser { Id = 84, Login = "new-account" };
            });

        bool authorized = await context.Service.Authorize(
            $"handoff=valid-handoff&state={Uri.EscapeDataString(expectedState)}");

        Assert.True(authorized);
        Assert.Equal(84, context.Account.UserId);
        Assert.Equal("new-account-token", context.CredentialStore.GetAccountToken(84));
        Assert.Null(context.CredentialStore.GetPendingToken());
    }

    [Theory]
    [Trait("Category", "ReleaseSecurity")]
    [InlineData("token=attacker-token")]
    [InlineData("handoff=attacker-handoff&state=wrong-state")]
    [InlineData("handoff=attacker-handoff&state=")]
    public async Task Authorize_RejectsMissingOrMismatchedReturnedState(string callback)
    {
        TestContext context = CreateContext();
        await context.Service.Authenticate();
        string expectedState = Assert.IsType<string>(context.CredentialStore.GetPendingState());
        string expectedVerifier = Assert.IsType<string>(context.CredentialStore.GetPendingVerifier());

        bool authorized = await context.Service.Authorize(callback);

        Assert.False(authorized);
        Assert.Equal(expectedState, context.CredentialStore.GetPendingState());
        Assert.Equal(expectedVerifier, context.CredentialStore.GetPendingVerifier());
        Assert.Null(context.CredentialStore.GetPendingToken());
        Assert.False(context.Service.Authenticated);
        Assert.Contains(context.Telemetry.Events, static entry =>
            entry.Name == "auth.error" && entry.Properties["error_kind"] == "invalid_callback");
        await context.GitHubClient.DidNotReceiveWithAnyArgs()
            .GetCurrentUserAsync(default!, default);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Authorize_MismatchedCallbackCannotConsumeLegitimatePendingAttempt()
    {
        TestContext context = CreateContext();
        await context.Service.Authenticate();
        string expectedState = Assert.IsType<string>(context.CredentialStore.GetPendingState());
        context.GitHubClient
            .GetCurrentUserAsync("valid-token", Arg.Any<CancellationToken>())
            .Returns(new GitHubUser { Id = 42, Login = "octocat" });
        context.HandoffClient.Token = "valid-token";

        Assert.False(await context.Service.Authorize("handoff=forged&state=wrong-state"));
        Assert.True(await context.Service.Authorize(
            $"handoff=valid-handoff&state={Uri.EscapeDataString(expectedState)}"));

        Assert.True(context.Service.Authenticated);
        Assert.Equal(1, context.HandoffClient.RedemptionCount);
        Assert.Null(context.CredentialStore.GetPendingState());
        Assert.Null(context.CredentialStore.GetPendingVerifier());
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Authorize_RejectsAmbiguousDuplicateReturnedState()
    {
        TestContext context = CreateContext();
        await context.Service.Authenticate();
        string expectedState = Assert.IsType<string>(context.CredentialStore.GetPendingState());

        bool authorized = await context.Service.Authorize(
            $"handoff=attacker-handoff&state={Uri.EscapeDataString(expectedState)}&state={Uri.EscapeDataString(expectedState)}");

        Assert.False(authorized);
        await context.GitHubClient.DidNotReceiveWithAnyArgs()
            .GetCurrentUserAsync(default!, default);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Authorize_AcceptsOnlyExactReturnedStateAndPersistsAccountToken()
    {
        TestContext context = CreateContext();
        await context.Service.Authenticate();
        string expectedState = Assert.IsType<string>(context.CredentialStore.GetPendingState());
        context.GitHubClient
            .GetCurrentUserAsync("valid-token", Arg.Any<CancellationToken>())
            .Returns(new GitHubUser { Id = 42, Login = "octocat" });
        context.HandoffClient.Token = "valid-token";

        bool authorized = await context.Service.Authorize(
            $"handoff=valid-handoff&state={Uri.EscapeDataString(expectedState)}");

        Assert.True(authorized);
        Assert.True(context.Service.Authenticated);
        Assert.Equal(42, context.Account.UserId);
        Assert.Equal("valid-token", context.CredentialStore.GetAccountToken(42));
        Assert.Null(context.CredentialStore.GetPendingState());
        Assert.Equal("valid-token", context.GitHubService.AccessToken);
        Assert.Contains(context.Telemetry.Events, static entry =>
            entry.Name == "auth.flow.started" && entry.Properties["source"] == "callback");
        Assert.Contains(context.Telemetry.Events, static entry =>
            entry.Name == "auth.flow.completed" && entry.Properties["result"] == "authenticated");
        Assert.DoesNotContain(context.Telemetry.Events.SelectMany(static entry => entry.Properties.Values),
            static value => value is "valid-token" or "octocat" or "42");
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Authorize_ExactRecentlyCompletedCallbackIsIdempotent()
    {
        TestContext context = CreateContext();
        await context.Service.Authenticate();
        string expectedState = Assert.IsType<string>(context.CredentialStore.GetPendingState());
        string callback = $"handoff=valid-handoff&state={Uri.EscapeDataString(expectedState)}";
        context.GitHubClient
            .GetCurrentUserAsync("valid-token", Arg.Any<CancellationToken>())
            .Returns(new GitHubUser { Id = 42, Login = "octocat" });
        context.HandoffClient.Token = "valid-token";

        Assert.True(await context.Service.Authorize(callback));
        Assert.True(await context.Service.Authorize(callback));

        Assert.True(context.Service.Authenticated);
        Assert.Equal(AuthSessionRecoveryState.None, context.Service.RecoveryState);
        Assert.Equal(1, context.HandoffClient.RedemptionCount);
        await context.GitHubClient.Received(1)
            .GetCurrentUserAsync("valid-token", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Authenticate_LaunchFailureCompletesFlowAfterStartedWithError()
    {
        TestContext context = CreateContext(new FailingLauncher());

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.Authenticate());

        Assert.Equal(
            [TelemetryTaxonomy.Results.Started, TelemetryTaxonomy.Results.Error],
            context.Telemetry.Events
                .Where(static entry => entry.Name is "auth.flow.started" or "auth.flow.completed")
                .Select(static entry => entry.Properties["result"]));
        RecordedTelemetryEvent terminal = Assert.Single(
            context.Telemetry.Events,
            static entry => entry.Name == "auth.flow.completed");
        Assert.Equal(TelemetryTaxonomy.ErrorKinds.Launch, terminal.Properties["error_kind"]);
    }

    [Fact]
    public async Task Authenticate_LaunchCancellationCompletesFlowAfterStartedWithCancelled()
    {
        TestContext context = CreateContext(new CancellingLauncher());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Service.Authenticate());

        Assert.Equal(
            [TelemetryTaxonomy.Results.Started, TelemetryTaxonomy.Results.Cancelled],
            context.Telemetry.Events
                .Where(static entry => entry.Name is "auth.flow.started" or "auth.flow.completed")
                .Select(static entry => entry.Properties["result"]));
        RecordedTelemetryEvent terminal = Assert.Single(
            context.Telemetry.Events,
            static entry => entry.Name == "auth.flow.completed");
        Assert.Equal(TelemetryTaxonomy.ErrorKinds.Cancelled, terminal.Properties["error_kind"]);
    }

    [Fact]
    public async Task RefreshAuthenticatedUser_UnexpectedFailureCompletesActionAfterStartedWithError()
    {
        TestContext context = CreateContext();
        context.CredentialStore.SavePendingToken("test-token");
        context.GitHubClient
            .GetCurrentUserAsync("test-token", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubUser>(new Exception("injected failure")));

        await Assert.ThrowsAsync<Exception>(
            () => context.Service.RefreshAuthenticatedUserAsync());

        RecordedTelemetryEvent[] refreshEvents = context.Telemetry.Events
            .Where(static entry =>
                entry.Name == "auth.action.executed" &&
                entry.Properties.TryGetValue("action", out string? action) &&
                action == TelemetryTaxonomy.Actions.RefreshUser)
            .ToArray();
        Assert.Equal(2, refreshEvents.Length);
        Assert.Equal(TelemetryTaxonomy.Results.Started, refreshEvents[0].Properties["result"]);
        Assert.Equal(TelemetryTaxonomy.Results.Error, refreshEvents[1].Properties["result"]);
        Assert.Equal(TelemetryTaxonomy.ErrorKinds.Unexpected, refreshEvents[1].Properties["error_kind"]);
    }

    [Fact]
    public async Task RefreshAuthenticatedUser_CancellationCompletesActionAfterStartedWithCancelled()
    {
        TestContext context = CreateContext();
        context.CredentialStore.SavePendingToken("test-token");
        context.GitHubClient
            .GetCurrentUserAsync("test-token", Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled<GitHubUser>(new CancellationToken(canceled: true)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Service.RefreshAuthenticatedUserAsync());

        RecordedTelemetryEvent[] refreshEvents = context.Telemetry.Events
            .Where(static entry =>
                entry.Name == "auth.action.executed" &&
                entry.Properties.TryGetValue("action", out string? action) &&
                action == TelemetryTaxonomy.Actions.RefreshUser)
            .ToArray();
        Assert.Equal(2, refreshEvents.Length);
        Assert.Equal(TelemetryTaxonomy.Results.Started, refreshEvents[0].Properties["result"]);
        Assert.Equal(TelemetryTaxonomy.Results.Cancelled, refreshEvents[1].Properties["result"]);
        Assert.Equal(TelemetryTaxonomy.ErrorKinds.Cancelled, refreshEvents[1].Properties["error_kind"]);
    }

    private static TestContext CreateContext(IExternalUriLauncher? launcher = null)
    {
        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        client.CreateLoginUri(
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(new Uri("https://github.test/login"));
        MemoryCredentialVaultBackend backend = new();
        AuthCredentialStore credentialStore = new(backend, new TestAppConfig());
        TestAccountService account = new();
        RecordingGitHubService githubService = new();
        RecordingTelemetryService telemetry = new();
        TestAuthHandoffClient handoffClient = new();
        AuthService service = new(
            new TestAppConfig(),
            account,
            client,
            githubService,
            new MemorySettingService(),
            new NavigationService(),
            launcher ?? new SuccessfulLauncher(),
            credentialStore,
            new AccountWorkQuiescence(),
            telemetry,
            handoffClient);
        return new TestContext(service, client, githubService, credentialStore, account, telemetry, handoffClient);
    }

    private sealed record TestContext(
        AuthService Service,
        IGitHubClientService GitHubClient,
        RecordingGitHubService GitHubService,
        AuthCredentialStore CredentialStore,
        TestAccountService Account,
        RecordingTelemetryService Telemetry,
        TestAuthHandoffClient HandoffClient);

    private sealed class TestAppConfig : IAppConfig
    {
        public Credential Credential { get; } = new()
        {
            ClientId = "security-test-client",
            AuthorizationCallbackUrl = "https://localhost:7284/authorize"
        };
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

    private sealed class SuccessfulLauncher : IExternalUriLauncher
    {
        public Task<bool> LaunchAsync(Uri uri, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FailingLauncher : IExternalUriLauncher
    {
        public Task<bool> LaunchAsync(Uri uri, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class CancellingLauncher : IExternalUriLauncher
    {
        public Task<bool> LaunchAsync(Uri uri, CancellationToken cancellationToken = default) =>
            Task.FromCanceled<bool>(new CancellationToken(canceled: true));
    }

    private sealed class TestAuthHandoffClient : IAuthHandoffClient
    {
        public string? Token { get; set; }

        public int RedemptionCount { get; private set; }

        public Task<string?> RedeemAsync(
            string? authorizationCallbackUrl,
            string handoff,
            string state,
            string verifier,
            CancellationToken cancellationToken = default)
        {
            RedemptionCount++;
            return Task.FromResult(Token);
        }
    }

    private sealed class MemoryCredentialVaultBackend : ICredentialVaultBackend
    {
        private readonly Dictionary<(string Resource, string UserName), string> _values = [];

        public string? Retrieve(string resource, string userName) =>
            _values.TryGetValue((resource, userName), out string? value) ? value : null;

        public void Store(string resource, string userName, string secret) =>
            _values[(resource, userName)] = secret;

        public void Remove(string resource, string userName) =>
            _values.Remove((resource, userName));
    }
}
