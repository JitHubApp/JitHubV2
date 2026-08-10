using System.Net;
using JitHub.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JitHub.Web.Tests;

public sealed class OAuthRedirectUriPolicyTests
{
    [Fact]
    public void ProductionAcceptsOnlyConfiguredCallbackIdentity()
    {
        OAuthRedirectUriPolicy policy = LoadPolicy(
            Environments.Production,
            new Dictionary<string, string?>
            {
                [OAuthRedirectUriPolicy.CallbackUrlSetting] = "https://auth.jithub.example/authorize"
            });

        Assert.Equal(
            "https://auth.jithub.example/authorize",
            policy.RequireAllowed("https://auth.jithub.example/authorize"));
        Assert.Throws<InvalidOperationException>(() =>
            policy.RequireAllowed("https://attacker.example/authorize"));
        Assert.Throws<InvalidOperationException>(() =>
            policy.RequireAllowed("http://auth.jithub.example/authorize"));
        Assert.Throws<InvalidOperationException>(() =>
            policy.RequireAllowed("https://auth.jithub.example/authorize?next=attacker"));
        Assert.Throws<InvalidOperationException>(() =>
            policy.RequireAllowed("https://auth.jithub.example/AUTHORIZE"));
        Assert.Throws<InvalidOperationException>(() =>
            policy.RequireAllowed("https://auth.jithub.example/authorize/"));
    }

    [Fact]
    public void ProductionRequiresConfiguredCallbackIdentity()
    {
        Assert.Throws<InvalidOperationException>(() =>
            LoadPolicy(Environments.Production, new Dictionary<string, string?>()));
    }

    [Theory]
    [InlineData("https://localhost:7284/authorize")]
    [InlineData("http://localhost:5280/authorize")]
    [InlineData("https://localhost:44396/authorize")]
    public void DevelopmentAcceptsOnlyKnownLoopbackCallbacks(string redirectUri)
    {
        OAuthRedirectUriPolicy policy = LoadPolicy(
            Environments.Development,
            new Dictionary<string, string?>());

        Assert.Equal(redirectUri, policy.RequireAllowed(redirectUri));
    }

    [Fact]
    public void DevelopmentAcceptsExplicitLoopbackCallbackAndRejectsArbitraryOrigins()
    {
        OAuthRedirectUriPolicy policy = LoadPolicy(
            Environments.Development,
            new Dictionary<string, string?>
            {
                [$"{OAuthRedirectUriPolicy.DevelopmentCallbackUrlsSection}:0"] =
                    "https://127.0.0.1:7443/authorize"
            });

        Assert.Equal(
            "https://127.0.0.1:7443/authorize",
            policy.RequireAllowed("https://127.0.0.1:7443/authorize"));
        Assert.Throws<InvalidOperationException>(() =>
            policy.RequireAllowed("https://localhost:7443/authorize"));
        Assert.Throws<InvalidOperationException>(() =>
            policy.RequireAllowed("https://example.test/authorize"));
    }

    [Fact]
    public async Task ExchangeServiceRevalidatesRedirectBeforeSendingGitHubRequest()
    {
        Dictionary<string, string?> settings = new()
        {
            [OAuthRedirectUriPolicy.CallbackUrlSetting] = "https://auth.jithub.example/authorize",
            ["JitHubClientId"] = "client-id",
            ["JithubAppSecret"] = "client-secret"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        TestHostEnvironment environment = new(Environments.Production);
        OAuthRedirectUriPolicy policy = OAuthRedirectUriPolicy.Load(configuration, environment);
        RecordingHandler handler = new();
        GithubAuthService service = new(
            new HttpClient(handler) { BaseAddress = new Uri("https://github.com/") },
            NullLogger<GithubAuthService>.Instance,
            configuration,
            environment,
            policy);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExchangeCodeForTokenAsync(
                "temporary-code",
                "https://attacker.example/authorize",
                CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ExchangeServiceSendsConfiguredRedirectIdentity()
    {
        Dictionary<string, string?> settings = new()
        {
            [OAuthRedirectUriPolicy.CallbackUrlSetting] = "https://auth.jithub.example/authorize",
            ["JitHubClientId"] = "client-id",
            ["JithubAppSecret"] = "client-secret"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        TestHostEnvironment environment = new(Environments.Production);
        OAuthRedirectUriPolicy policy = OAuthRedirectUriPolicy.Load(configuration, environment);
        RecordingHandler handler = new();
        GithubAuthService service = new(
            new HttpClient(handler) { BaseAddress = new Uri("https://github.com/") },
            NullLogger<GithubAuthService>.Instance,
            configuration,
            environment,
            policy);

        string token = await service.ExchangeCodeForTokenAsync(
            "temporary-code",
            "https://auth.jithub.example/authorize",
            CancellationToken.None);

        Assert.Equal("test-token", token);
        Assert.Contains("redirect_uri=https%3A%2F%2Fauth.jithub.example%2Fauthorize", handler.RequestBody, StringComparison.Ordinal);
    }

    private static OAuthRedirectUriPolicy LoadPolicy(
        string environmentName,
        IReadOnlyDictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        return OAuthRedirectUriPolicy.Load(configuration, new TestHostEnvironment(environmentName));
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "JitHub.Web.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"test-token\"}")
            };
        }
    }
}
