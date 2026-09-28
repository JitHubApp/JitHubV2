using System.Net;
using System.Net.Http;
using System.Text;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubDeviceFlowClientTests
{
    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task DeviceFlow_HonorsPollingIntervalAndSlowDownWithoutClientSecret()
    {
        QueueHandler handler = new(
            """{"device_code":"private-device-code","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""",
            """{"error":"authorization_pending"}""",
            """{"error":"slow_down","interval":10}""",
            """{"access_token":"access","refresh_token":"refresh","expires_in":28800,"refresh_token_expires_in":15552000}""");
        ManualClock clock = new();
        List<TimeSpan> delays = [];
        using GitHubDeviceFlowClient client = new(
            new HttpClient(handler),
            (delay, _) =>
            {
                delays.Add(delay);
                clock.Advance(delay);
                return Task.CompletedTask;
            },
            clock,
            ownsClient: true);

        DeviceAuthorizationChallenge challenge = await client.RequestCodeAsync(
            "public-client", ["repo", "offline_access"]);
        GitHubTokenSession pair = await client.PollAsync("public-client", challenge);

        Assert.Equal("ABCD-1234", challenge.UserCode);
        Assert.Equal("access", pair.AccessToken);
        Assert.Equal("refresh", pair.RefreshToken);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], delays);
        Assert.Equal(4, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.DoesNotContain("client_secret", request.Body));
        Assert.Contains("offline_access", handler.Requests[0].Body);
        Assert.All(handler.Requests, request => Assert.Equal("application/json", request.Accept));
    }

    [Fact]
    public async Task DeviceFlow_RejectsUnexpectedVerificationDomain()
    {
        QueueHandler handler = new(
            """{"device_code":"device","user_code":"ABCD-1234","verification_uri":"https://lookalike.example/device","expires_in":900,"interval":5}""");
        using GitHubDeviceFlowClient client = new(new HttpClient(handler), Task.Delay, TimeProvider.System, true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RequestCodeAsync("public-client", ["repo"]));
    }

    [Fact]
    public async Task Refresh_UsesDeviceIssuedRefreshTokenWithoutClientSecret()
    {
        QueueHandler handler = new(
            """{"access_token":"next-access","refresh_token":"next-refresh","expires_in":28800,"refresh_token_expires_in":15552000}""");
        using GitHubDeviceFlowClient client = new(new HttpClient(handler), Task.Delay, TimeProvider.System, true);

        GitHubTokenSession pair = await client.RefreshAsync("public-client", "old-refresh");

        Assert.Equal("next-access", pair.AccessToken);
        Assert.Equal("next-refresh", pair.RefreshToken);
        Assert.Contains("grant_type=refresh_token", handler.Requests[0].Body);
        Assert.DoesNotContain("client_secret", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Poll_DenialTerminatesWithoutReturningToken()
    {
        QueueHandler handler = new("""{"error":"access_denied"}""");
        ManualClock clock = new();
        using GitHubDeviceFlowClient client = new(
            new HttpClient(handler), (delay, _) =>
            {
                clock.Advance(delay);
                return Task.CompletedTask;
            }, clock, true);
        DeviceAuthorizationChallenge challenge = new(
            "device", "ABCD-1234", new Uri("https://github.com/login/device"),
            clock.GetUtcNow().AddMinutes(15), TimeSpan.FromSeconds(5));

        DeviceFlowException error = await Assert.ThrowsAsync<DeviceFlowException>(() =>
            client.PollAsync("public-client", challenge));

        Assert.Equal("access_denied", error.Code);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class QueueHandler(params string[] replies) : HttpMessageHandler
    {
        private readonly Queue<string> _replies = new(replies);
        public List<(string Body, string? Accept)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((body, request.Headers.Accept.SingleOrDefault()?.MediaType));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_replies.Dequeue(), Encoding.UTF8, "application/json")
            };
        }
    }
}
