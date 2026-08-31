using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubClientRateLimitTests
{
    [Fact]
    public async Task BranchProbe_MapsRetryAfterToRateLimitExceptionWithoutCappingDelay()
    {
        using HttpClient httpClient = new(new StaticResponseHandler(() =>
        {
            HttpResponseMessage response = new(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"message\":\"slow down\"}", Encoding.UTF8, "application/json")
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return response;
        }));
        GitHubClientService client = new(httpClient);

        GitHubRateLimitException error = await Assert.ThrowsAsync<GitHubRateLimitException>(() =>
            client.GetBranchesAsync("token", "owner", "repo", cancellationToken: CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(90), error.RetryDelay);
    }

    [Fact]
    public async Task BranchProbe_MapsPrimaryRateLimitResetToRateLimitException()
    {
        DateTimeOffset resetAt = DateTimeOffset.UtcNow.AddMinutes(2);
        using HttpClient httpClient = new(new StaticResponseHandler(() =>
        {
            HttpResponseMessage response = new(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"message\":\"rate limited\"}", Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", resetAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        }));
        GitHubClientService client = new(httpClient);

        GitHubRateLimitException error = await Assert.ThrowsAsync<GitHubRateLimitException>(() =>
            client.GetBranchesAsync("token", "owner", "repo", cancellationToken: CancellationToken.None));

        Assert.InRange(error.RetryDelay, TimeSpan.FromSeconds(115), TimeSpan.FromSeconds(125));
    }

    [Fact]
    public async Task BranchProbe_DoesNotMisclassifyOrdinaryForbiddenResponseAsRateLimit()
    {
        using HttpClient httpClient = new(new StaticResponseHandler(() => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"message\":\"Resource not accessible by integration\"}", Encoding.UTF8, "application/json")
        }));
        GitHubClientService client = new(httpClient);

        GitHubApiException error = await Assert.ThrowsAsync<GitHubApiException>(() =>
            client.GetBranchesAsync("token", "owner", "repo", cancellationToken: CancellationToken.None));

        Assert.IsNotType<GitHubRateLimitException>(error);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task ForkCreationAndBranchProbe_PropagateCallerCancellationToHttpTransport()
    {
        await AssertTransportCancellationAsync((client, token) =>
            client.ForkRepositoryAsync("token", "owner", "repo", token));
        await AssertTransportCancellationAsync((client, token) =>
            client.GetBranchesAsync("token", "owner", "repo", cancellationToken: token));
    }

    private static async Task AssertTransportCancellationAsync(
        Func<GitHubClientService, CancellationToken, Task> action)
    {
        CancellationProbeHandler handler = new();
        using HttpClient httpClient = new(handler);
        GitHubClientService client = new(httpClient);
        using CancellationTokenSource cancellation = new();

        Task request = action(client, cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(handler.ObservedToken.CanBeCanceled);
        Assert.True(handler.WasCanceled);
    }

    private sealed class StaticResponseHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory());
        }
    }

    private sealed class CancellationProbeHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ObservedToken { get; private set; }

        public bool WasCanceled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ObservedToken = cancellationToken;
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                WasCanceled = true;
                throw;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
