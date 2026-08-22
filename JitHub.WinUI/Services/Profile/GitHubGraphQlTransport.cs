using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public sealed class GitHubGraphQlTransport : IGitHubGraphQlTransport
{
    private readonly HttpClient _httpClient;

    public GitHubGraphQlTransport()
        : this(CreateDefaultHttpClient())
    {
    }

    internal GitHubGraphQlTransport(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= new Uri("https://api.github.com/");
    }

    public async Task<GitHubGraphQlResponse<T>> SendAsync<T>(
        string accessToken,
        GitHubGraphQlRequest request,
        JsonTypeInfo<GitHubGraphQlResponse<T>> responseJsonTypeInfo,
        CancellationToken cancellationToken = default)
        where T : class
    {
        using HttpRequestMessage message = new(HttpMethod.Post, "graphql");
        message.Headers.UserAgent.Add(new ProductInfoHeaderValue("JitHub", "1.0"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(accessToken) &&
            !GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        message.Content = JsonContent.Create(
            request,
            GitHubJsonSerializerContext.Default.GitHubGraphQlRequest);

        using HttpResponseMessage response =
            await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        GitHubGraphQlResponseHeaders headers = ReadHeaders(response);
        if (!response.IsSuccessStatusCode)
        {
            string messageText = await ReadErrorMessageAsync(response, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new GitHubAuthenticationException(messageText);
            }

            TimeSpan? retryDelay = GitHubRetryPolicy.CalculateRetryDelay(
                response.StatusCode,
                headers.RateLimitRemaining,
                headers.RateLimitReset,
                headers.RetryAfter,
                DateTimeOffset.UtcNow);
            if (retryDelay is TimeSpan delay)
            {
                throw new GitHubRateLimitException(
                    response.StatusCode,
                    messageText,
                    delay,
                    headers.RateLimitRemaining,
                    headers.RateLimitReset,
                    headers.RetryAfter,
                    headers.RateLimitResource);
            }

            throw new GitHubApiException(response.StatusCode, messageText);
        }

        GitHubGraphQlResponse<T>? payload =
            await response.Content.ReadFromJsonAsync(responseJsonTypeInfo, cancellationToken);
        if (payload is null)
        {
            throw new GitHubApiException(HttpStatusCode.OK, "GitHub GraphQL returned an empty payload.");
        }

        payload.RateLimitRemaining = headers.RateLimitRemaining;
        payload.RateLimitReset = headers.RateLimitReset;
        payload.RetryAfter = headers.RetryAfter;
        payload.RateLimitResource = headers.RateLimitResource;

        if (payload.Errors is { Length: > 0 } errors)
        {
            string errorText = string.Join(
                " ",
                errors.Select(static error => error.Message).Where(static message => !string.IsNullOrWhiteSpace(message)));
            string normalizedError = string.IsNullOrWhiteSpace(errorText)
                ? "GitHub GraphQL returned an error."
                : errorText;
            if (TryGetSuccessfulResponseRateLimitDelay(headers, normalizedError) is TimeSpan retryDelay)
            {
                throw new GitHubRateLimitException(
                    response.StatusCode,
                    normalizedError,
                    retryDelay,
                    headers.RateLimitRemaining,
                    headers.RateLimitReset,
                    headers.RetryAfter,
                    headers.RateLimitResource);
            }

            throw new GitHubApiException(
                HttpStatusCode.OK,
                normalizedError);
        }

        return payload;
    }

    private static HttpClient CreateDefaultHttpClient() => new()
    {
        BaseAddress = new Uri("https://api.github.com/")
    };

    private static string? TryGetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()
            : response.Content.Headers.TryGetValues(name, out values)
                ? values.FirstOrDefault()
                : null;

    private static int? TryGetIntHeader(HttpResponseMessage response, string name) =>
        int.TryParse(TryGetHeader(response, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    private static DateTimeOffset? TryGetUnixTimestampHeader(HttpResponseMessage response, string name)
    {
        if (!long.TryParse(TryGetHeader(response, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static GitHubGraphQlResponseHeaders ReadHeaders(HttpResponseMessage response)
    {
        TimeSpan? retryAfter;
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
        {
            retryAfter = delta;
        }
        else
        {
            retryAfter = response.Headers.RetryAfter?.Date is DateTimeOffset date
                ? date - DateTimeOffset.UtcNow
                : null;
        }

        return new GitHubGraphQlResponseHeaders(
            TryGetIntHeader(response, "X-RateLimit-Remaining"),
            TryGetUnixTimestampHeader(response, "X-RateLimit-Reset"),
            retryAfter,
            TryGetHeader(response, "X-RateLimit-Resource"));
    }

    private static TimeSpan? TryGetSuccessfulResponseRateLimitDelay(
        GitHubGraphQlResponseHeaders headers,
        string errorText)
    {
        bool isRateLimitError = headers.RateLimitRemaining == 0 ||
            errorText.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            errorText.Contains("rate-limit", StringComparison.OrdinalIgnoreCase);
        if (!isRateLimitError)
        {
            return null;
        }

        if (headers.RetryAfter is TimeSpan retryAfter)
        {
            return retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.Zero;
        }

        if (headers.RateLimitReset is DateTimeOffset resetAt)
        {
            TimeSpan resetDelay = resetAt - DateTimeOffset.UtcNow;
            return resetDelay > TimeSpan.Zero ? resetDelay : TimeSpan.Zero;
        }

        return GitHubRetryPolicy.DefaultSecondaryRateLimitDelay;
    }

    private sealed record GitHubGraphQlResponseHeaders(
        int? RateLimitRemaining,
        DateTimeOffset? RateLimitReset,
        TimeSpan? RetryAfter,
        string? RateLimitResource);

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("message", out JsonElement messageElement))
            {
                return messageElement.GetString() ?? $"GitHub GraphQL request failed with status code {(int)response.StatusCode}.";
            }
        }
        catch
        {
        }

        return $"GitHub GraphQL request failed with status code {(int)response.StatusCode}.";
    }
}
