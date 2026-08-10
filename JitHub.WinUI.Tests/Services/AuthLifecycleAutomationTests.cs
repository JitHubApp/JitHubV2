using System.Net;
using System.Net.Http.Headers;
using JitHub.Models;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class AuthLifecycleAutomationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "JitHub-AuthLifecycleAutomationTests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(AuthLifecycleScenario.Cancel)]
    [InlineData(AuthLifecycleScenario.InvalidState)]
    [InlineData(AuthLifecycleScenario.ExpiredToken)]
    [InlineData(AuthLifecycleScenario.NotificationReconnect)]
    [InlineData(AuthLifecycleScenario.OfflineLaunch)]
    [InlineData(AuthLifecycleScenario.ProtocolReactivation)]
    [InlineData(AuthLifecycleScenario.MultiAccountCleanup)]
    public void KnownScenariosAreExplicitlyAllowlisted(string scenario)
    {
        Assert.True(AuthLifecycleAutomationContext.IsKnownScenario(scenario));
    }

    [Fact]
    public void FileCredentialVaultPersistsAndRemovesOneAccountWithoutTouchingAnother()
    {
        string path = Path.Combine(_root, "vault", "credentials.vault");
        var first = new FileCredentialVaultBackend(path);
        first.Store("client", "101", "primary");
        first.Store("client", "202", "secondary");

        var reopened = new FileCredentialVaultBackend(path);
        Assert.Equal("primary", reopened.Retrieve("client", "101"));
        Assert.Equal("secondary", reopened.Retrieve("client", "202"));

        reopened.Remove("client", "101");

        Assert.Null(first.Retrieve("client", "101"));
        Assert.Equal("secondary", first.Retrieve("client", "202"));
    }

    [Fact]
    public void MultiAccountScenarioSeedsCurrentAndSecondaryCredentialsOnlyOnce()
    {
        AuthLifecycleAutomationContext context = CreateContext(AuthLifecycleScenario.MultiAccountCleanup);
        var settings = new MemorySettingService();
        var account = new TestAccountService();
        var credentialStore = new AuthCredentialStore(
            new FileCredentialVaultBackend(context.CredentialPath),
            new TestAppConfig());

        context.Seed(settings, account, credentialStore);
        credentialStore.RemoveAccountToken(AuthLifecycleAutomationContext.PrimaryUserId);
        context.Seed(settings, account, credentialStore);

        Assert.Equal(AuthLifecycleAutomationContext.PrimaryUserId, account.UserId);
        Assert.Null(credentialStore.GetAccountToken(AuthLifecycleAutomationContext.PrimaryUserId));
        Assert.Equal(
            AuthLifecycleAutomationContext.SecondaryToken,
            credentialStore.GetAccountToken(AuthLifecycleAutomationContext.SecondaryUserId));
    }

    [Fact]
    public async Task ExpiredTokenTransportReturnsUnauthorized()
    {
        AuthLifecycleAutomationContext context = CreateContext(AuthLifecycleScenario.ExpiredToken);
        using var client = new HttpClient(context.CreateHttpMessageHandler())
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AuthLifecycleAutomationContext.ExpiredToken);

        using HttpResponseMessage response = await client.GetAsync("user");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OfflineTransportFailsWithoutDeletingLocalState()
    {
        AuthLifecycleAutomationContext context = CreateContext(AuthLifecycleScenario.OfflineLaunch);
        using var client = new HttpClient(context.CreateHttpMessageHandler())
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("user"));
        Assert.Contains("http.offline", File.ReadAllText(context.MarkerPath));
    }

    [Fact]
    public async Task NotificationScenarioExposesScopeFailureAndOmitsScopeHeader()
    {
        AuthLifecycleAutomationContext context = CreateContext(AuthLifecycleScenario.NotificationReconnect);
        using var client = new HttpClient(context.CreateHttpMessageHandler())
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        using HttpResponseMessage user = await client.GetAsync("user");
        using HttpResponseMessage notifications = await client.GetAsync("notifications?all=false");

        Assert.Equal(["user, repo"], user.Headers.GetValues("X-OAuth-Scopes"));
        Assert.Equal(HttpStatusCode.Forbidden, notifications.StatusCode);
    }

    [Fact]
    public async Task CancelScenarioUsesRealLauncherCancellationContract()
    {
        AuthLifecycleAutomationContext context = CreateContext(AuthLifecycleScenario.Cancel);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.CreateUriLauncher().LaunchAsync(new Uri("https://github.com/login/oauth/authorize")));

        string markers = File.ReadAllText(context.MarkerPath);
        Assert.Contains("oauth.launch.requested", markers);
        Assert.Contains("oauth.launch.cancelled", markers);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AuthLifecycleAutomationContext CreateContext(string scenario) =>
        AuthLifecycleAutomationContext.CreateForTests(scenario, Path.Combine(_root, scenario, "Local"));

    private sealed class TestAccountService : IAccountService
    {
        public long UserId { get; private set; }

        public void RemoveUser() => UserId = 0;

        public void SaveUser(long userId) => UserId = userId;

        public long GetUser() => UserId;
    }

    private sealed class TestAppConfig : IAppConfig
    {
        public Credential Credential { get; } = new()
        {
            ClientId = "auth-lifecycle-tests",
            AuthorizationCallbackUrl = "jithub-dev://auth"
        };
    }
}
