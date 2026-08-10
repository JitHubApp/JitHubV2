using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class OAuthAuthorizationFlowTests
{
    private static readonly Uri AuthorizationUri = new("https://github.com/login/oauth/authorize?scope=delete_repo");

    [Fact]
    public async Task EnsureScopesAsync_GrantedScopeDoesNotBuildOrLaunchAuthorization()
    {
        using HttpClient httpClient = CreateClient(response =>
        {
            response.Headers.Add("X-OAuth-Scopes", "user, repo, delete_repo");
        });
        GitHubClientService gitHubClient = new(httpClient);
        RecordingLauncher launcher = new();
        int uriFactoryCalls = 0;

        OAuthAuthorizationResult result = await OAuthAuthorizationFlow.EnsureScopesAsync(
            gitHubClient,
            launcher,
            "existing-token",
            ["delete_repo"],
            () =>
            {
                uriFactoryCalls++;
                return AuthorizationUri;
            });

        Assert.Equal(OAuthAuthorizationResult.AlreadyGranted, result);
        Assert.Equal(0, uriFactoryCalls);
        Assert.Empty(launcher.LaunchedUris);
    }

    [Fact]
    public async Task EnsureScopesAsync_MissingScopeLaunchesExplicitAuthorization()
    {
        using HttpClient httpClient = CreateClient(response =>
        {
            response.Headers.Add("X-OAuth-Scopes", "user, repo, notifications");
        });
        GitHubClientService gitHubClient = new(httpClient);
        RecordingLauncher launcher = new();

        OAuthAuthorizationResult result = await OAuthAuthorizationFlow.EnsureScopesAsync(
            gitHubClient,
            launcher,
            "existing-token",
            ["delete_repo"],
            () => AuthorizationUri);

        Assert.Equal(OAuthAuthorizationResult.AuthorizationLaunched, result);
        Assert.Equal(AuthorizationUri, Assert.Single(launcher.LaunchedUris));
    }

    [Fact]
    public async Task EnsureScopesAsync_MissingScopeHeaderUsesSafeAuthorizationFallback()
    {
        using HttpClient httpClient = CreateClient();
        GitHubClientService gitHubClient = new(httpClient);
        RecordingLauncher launcher = new();

        OAuthAuthorizationResult result = await OAuthAuthorizationFlow.EnsureScopesAsync(
            gitHubClient,
            launcher,
            "existing-token",
            ["delete_repo"],
            () => AuthorizationUri);

        Assert.Equal(OAuthAuthorizationResult.AuthorizationLaunched, result);
        Assert.Single(launcher.LaunchedUris);
    }

    [Fact]
    public async Task EnsureScopesAsync_RejectedTokenDoesNotOpenAuthorization()
    {
        using HttpClient httpClient = new(new StubHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"message\":\"Bad credentials\"}")
            }));
        GitHubClientService gitHubClient = new(httpClient);
        RecordingLauncher launcher = new();

        OAuthAuthorizationResult result = await OAuthAuthorizationFlow.EnsureScopesAsync(
            gitHubClient,
            launcher,
            "rejected-token",
            ["delete_repo"],
            () => AuthorizationUri);

        Assert.Equal(OAuthAuthorizationResult.AuthenticationRejected, result);
        Assert.Empty(launcher.LaunchedUris);
    }

    [Fact]
    public async Task EnsureScopesAsync_ReportsLauncherFailure()
    {
        using HttpClient httpClient = CreateClient();
        GitHubClientService gitHubClient = new(httpClient);
        RecordingLauncher launcher = new() { LaunchResult = false };

        OAuthAuthorizationResult result = await OAuthAuthorizationFlow.EnsureScopesAsync(
            gitHubClient,
            launcher,
            null,
            ["delete_repo"],
            () => AuthorizationUri);

        Assert.Equal(OAuthAuthorizationResult.LaunchFailed, result);
        Assert.Single(launcher.LaunchedUris);
    }

    [Fact]
    public async Task EnsureScopesAsync_RetryWithUpgradedTokenCompletesWithoutSecondLaunch()
    {
        using HttpClient initialHttpClient = CreateClient(response =>
        {
            response.Headers.Add("X-OAuth-Scopes", "user, repo");
        });
        RecordingLauncher launcher = new();

        OAuthAuthorizationResult initialResult = await OAuthAuthorizationFlow.EnsureScopesAsync(
            new GitHubClientService(initialHttpClient),
            launcher,
            "original-token",
            ["delete_repo"],
            () => AuthorizationUri);

        using HttpClient upgradedHttpClient = CreateClient(response =>
        {
            response.Headers.Add("X-OAuth-Scopes", "user, repo, delete_repo");
        });
        OAuthAuthorizationResult retryResult = await OAuthAuthorizationFlow.EnsureScopesAsync(
            new GitHubClientService(upgradedHttpClient),
            launcher,
            "upgraded-token",
            ["delete_repo"],
            () => AuthorizationUri);

        Assert.Equal(OAuthAuthorizationResult.AuthorizationLaunched, initialResult);
        Assert.Equal(OAuthAuthorizationResult.AlreadyGranted, retryResult);
        Assert.Single(launcher.LaunchedUris);
    }

    [Fact]
    public async Task EnsureScopesAsync_PropagatesNetworkFailureWithoutLaunching()
    {
        using HttpClient httpClient = new(new StubHttpMessageHandler(
            _ => throw new HttpRequestException("offline")));
        GitHubClientService gitHubClient = new(httpClient);
        RecordingLauncher launcher = new();

        await Assert.ThrowsAsync<HttpRequestException>(() => OAuthAuthorizationFlow.EnsureScopesAsync(
            gitHubClient,
            launcher,
            "existing-token",
            ["delete_repo"],
            () => AuthorizationUri));

        Assert.Empty(launcher.LaunchedUris);
    }

    private static HttpClient CreateClient(Action<HttpResponseMessage>? configure = null) =>
        new(new StubHttpMessageHandler(_ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
            configure?.Invoke(response);
            return response;
        }));

    private sealed class RecordingLauncher : IExternalUriLauncher
    {
        public bool LaunchResult { get; set; } = true;

        public System.Collections.Generic.List<Uri> LaunchedUris { get; } = [];

        public Task<bool> LaunchAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LaunchedUris.Add(uri);
            return Task.FromResult(LaunchResult);
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_handler(request));
        }
    }
}
