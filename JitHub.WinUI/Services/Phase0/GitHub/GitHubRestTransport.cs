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

namespace JitHub.Services;

public sealed class GitHubRestTransport : IGitHubRestTransport
{
    private static readonly Uri GitHubApiOrigin = new("https://api.github.com/");
    private readonly HttpClient _httpClient;

    public GitHubRestTransport()
        : this(CreateDefaultHttpClient())
    {
    }

    internal GitHubRestTransport(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= GitHubApiOrigin;
        EnsureTrustedApiOrigin(_httpClient.BaseAddress);
    }

    public async Task<GitHubRestResponse<T>> SendJsonAsync<T>(
        GitHubRestRequest request,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken = default)
        where T : class
    {
        using HttpRequestMessage httpRequest = CreateRequestMessage(request, _httpClient.BaseAddress!);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        DateTimeOffset fetchedAt = DateTimeOffset.UtcNow;
        GitHubResponseHeaders headers = ReadHeaders(response);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new GitHubRestResponse<T>(
                response.StatusCode,
                Payload: default,
                IsNotModified: true,
                headers.ETag,
                headers.LastModified,
                headers.Link,
                headers.RateLimitRemaining,
                headers.RateLimitReset,
                headers.RetryAfter,
                fetchedAt,
                headers.RateLimitResource);
        }

        if (response.StatusCode == HttpStatusCode.NotFound && request.AcceptNotFound)
        {
            return new GitHubRestResponse<T>(
                response.StatusCode,
                Payload: default,
                IsNotModified: false,
                headers.ETag,
                headers.LastModified,
                headers.Link,
                headers.RateLimitRemaining,
                headers.RateLimitReset,
                headers.RetryAfter,
                fetchedAt,
                headers.RateLimitResource);
        }

        await EnsureSuccessAsync(response, headers, cancellationToken);

        T? payload = default;
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
        {
            return new GitHubRestResponse<T>(
                response.StatusCode,
                payload,
                IsNotModified: false,
                headers.ETag,
                headers.LastModified,
                headers.Link,
                headers.RateLimitRemaining,
                headers.RateLimitReset,
                headers.RetryAfter,
                fetchedAt,
                headers.RateLimitResource);
        }

        try
        {
            payload = await response.Content.ReadFromJsonAsync(jsonTypeInfo, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException(HttpStatusCode.OK, $"GitHub returned an invalid payload: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            throw new GitHubApiException(HttpStatusCode.OK, $"GitHub returned an unsupported payload: {ex.Message}");
        }

        return new GitHubRestResponse<T>(
            response.StatusCode,
            payload,
            IsNotModified: false,
            headers.ETag,
            headers.LastModified,
            headers.Link,
            headers.RateLimitRemaining,
            headers.RateLimitReset,
            headers.RetryAfter,
            fetchedAt,
            headers.RateLimitResource);
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        HttpClient httpClient = new()
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        return httpClient;
    }

    private static HttpRequestMessage CreateRequestMessage(GitHubRestRequest request, Uri baseAddress)
    {
        Uri requestUri = ResolveTrustedRequestUri(baseAddress, request.RelativePath);
        HttpRequestMessage message = new(request.Method, requestUri);
        message.Headers.UserAgent.Add(new ProductInfoHeaderValue("JitHub", "1.0"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            string.IsNullOrWhiteSpace(request.AcceptMediaType)
                ? "application/vnd.github+json"
                : request.AcceptMediaType));
        message.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        if (!string.IsNullOrWhiteSpace(request.AccessToken) &&
            !GitHubAuthenticationConstants.IsPublicAccessToken(request.AccessToken))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.AccessToken);
        }

        if (!string.IsNullOrWhiteSpace(request.ETag))
        {
            if (EntityTagHeaderValue.TryParse(request.ETag, out EntityTagHeaderValue? entityTag))
            {
                message.Headers.IfNoneMatch.Add(entityTag);
            }
            else
            {
                message.Headers.TryAddWithoutValidation("If-None-Match", request.ETag);
            }
        }

        if (request.LastModified is DateTimeOffset lastModified)
        {
            message.Headers.IfModifiedSince = lastModified;
        }

        return message;
    }

    private static Uri ResolveTrustedRequestUri(Uri baseAddress, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string candidate = relativePath.Trim();
        if (candidate.StartsWith("//", StringComparison.Ordinal) ||
            candidate.StartsWith("\\\\", StringComparison.Ordinal) ||
            candidate.Contains('\\') ||
            Uri.TryCreate(candidate, UriKind.Absolute, out _))
        {
            throw new ArgumentException("GitHub REST paths must be relative to the trusted API origin.", nameof(relativePath));
        }

        Uri resolved = new(baseAddress, candidate);
        EnsureTrustedApiOrigin(resolved);
        return resolved;
    }

    private static void EnsureTrustedApiOrigin(Uri uri)
    {
        bool trusted =
            uri.IsAbsoluteUri &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(uri.Host, GitHubApiOrigin.Host, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == GitHubApiOrigin.Port &&
            string.IsNullOrEmpty(uri.UserInfo);
        if (!trusted)
        {
            throw new InvalidOperationException("GitHub REST transport is restricted to https://api.github.com.");
        }
    }

    private static GitHubResponseHeaders ReadHeaders(HttpResponseMessage response)
    {
        string? etag = response.Headers.ETag?.Tag;
        DateTimeOffset? lastModified = response.Content.Headers.LastModified ?? response.Headers.Date;
        string? link = TryGetHeader(response, "Link");
        int? rateLimitRemaining = TryGetIntHeader(response, "X-RateLimit-Remaining");
        DateTimeOffset? rateLimitReset = TryGetUnixTimestampHeader(response, "X-RateLimit-Reset");
        string? rateLimitResource = TryGetHeader(response, "X-RateLimit-Resource");
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is DateTimeOffset retryAfterDate)
        {
            retryAfter = retryAfterDate - DateTimeOffset.UtcNow;
        }

        return new GitHubResponseHeaders(
            etag,
            lastModified,
            link,
            rateLimitRemaining,
            rateLimitReset,
            retryAfter,
            rateLimitResource);
    }

    private static string? TryGetHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            return values.FirstOrDefault();
        }

        return response.Content.Headers.TryGetValues(name, out values)
            ? values.FirstOrDefault()
            : null;
    }

    private static int? TryGetIntHeader(HttpResponseMessage response, string name)
    {
        string? value = TryGetHeader(response, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset? TryGetUnixTimestampHeader(HttpResponseMessage response, string name)
    {
        string? value = TryGetHeader(response, name);
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        GitHubResponseHeaders headers,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? message = null;
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("message", out JsonElement messageElement))
            {
                message = messageElement.GetString();
            }
        }
        catch (JsonException)
        {
        }
        catch (NotSupportedException)
        {
        }

        message = string.IsNullOrWhiteSpace(message)
            ? $"GitHub request failed with status code {(int)response.StatusCode}."
            : message;

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new GitHubAuthenticationException(message);
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
                message,
                delay,
                headers.RateLimitRemaining,
                headers.RateLimitReset,
                headers.RetryAfter,
                headers.RateLimitResource);
        }

        throw new GitHubApiException(response.StatusCode, message);
    }

    private sealed record GitHubResponseHeaders(
        string? ETag,
        DateTimeOffset? LastModified,
        string? Link,
        int? RateLimitRemaining,
        DateTimeOffset? RateLimitReset,
        TimeSpan? RetryAfter,
        string? RateLimitResource);
}
