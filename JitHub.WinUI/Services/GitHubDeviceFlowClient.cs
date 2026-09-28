using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public sealed record DeviceAuthorizationChallenge(
    string DeviceCode,
    string UserCode,
    Uri VerificationUri,
    DateTimeOffset ExpiresAt,
    TimeSpan PollInterval);

public sealed record GitHubTokenSession(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? AccessTokenExpiresAt,
    DateTimeOffset? RefreshTokenExpiresAt,
    IReadOnlyCollection<string>? GrantedScopes = null);

public interface IGitHubDeviceFlowClient
{
    Task<DeviceAuthorizationChallenge> RequestCodeAsync(
        string clientId,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default);

    Task<GitHubTokenSession> PollAsync(
        string clientId,
        DeviceAuthorizationChallenge challenge,
        CancellationToken cancellationToken = default);

    Task<GitHubTokenSession> RefreshAsync(
        string clientId,
        string refreshToken,
        CancellationToken cancellationToken = default);
}

public sealed partial class GitHubDeviceFlowClient : IGitHubDeviceFlowClient, IDisposable
{
    private static readonly Uri CodeEndpoint = new("https://github.com/login/device/code");
    private static readonly Uri TokenEndpoint = new("https://github.com/login/oauth/access_token");
    private static readonly Uri VerificationEndpoint = new("https://github.com/login/device");
    private readonly HttpClient _httpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _clock;
    private readonly bool _ownsClient;

    public GitHubDeviceFlowClient()
        : this(new HttpClient(), Task.Delay, TimeProvider.System, ownsClient: true)
    {
    }

    internal GitHubDeviceFlowClient(
        HttpClient httpClient,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeProvider clock,
        bool ownsClient = false)
    {
        _httpClient = httpClient;
        _delay = delay;
        _clock = clock;
        _ownsClient = ownsClient;
    }

    public async Task<DeviceAuthorizationChallenge> RequestCodeAsync(
        string clientId,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(scopes);
        using JsonDocument response = await PostAsync(CodeEndpoint,
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = string.Join(' ', scopes)
            }, cancellationToken).ConfigureAwait(false);
        JsonElement root = response.RootElement;
        ThrowIfError(root);
        string deviceCode = RequiredString(root, "device_code");
        string userCode = RequiredString(root, "user_code");
        Uri verificationUri = new(RequiredString(root, "verification_uri"), UriKind.Absolute);
        if (verificationUri != VerificationEndpoint)
        {
            throw new InvalidOperationException("GitHub returned an unexpected verification address.");
        }

        int lifetime = RequiredPositiveInt(root, "expires_in");
        int interval = RequiredPositiveInt(root, "interval");
        return new DeviceAuthorizationChallenge(
            deviceCode, userCode, verificationUri,
            _clock.GetUtcNow().AddSeconds(lifetime), TimeSpan.FromSeconds(interval));
    }

    public async Task<GitHubTokenSession> PollAsync(
        string clientId,
        DeviceAuthorizationChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(challenge);
        TimeSpan interval = challenge.PollInterval;
        while (_clock.GetUtcNow() < challenge.ExpiresAt)
        {
            await _delay(interval, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_clock.GetUtcNow() >= challenge.ExpiresAt)
            {
                break;
            }

            using JsonDocument response = await PostAsync(TokenEndpoint,
                new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["device_code"] = challenge.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                }, cancellationToken).ConfigureAwait(false);
            JsonElement root = response.RootElement;
            if (root.TryGetProperty("error", out JsonElement errorElement))
            {
                string? error = errorElement.GetString();
                if (error == "authorization_pending")
                {
                    continue;
                }

                if (error == "slow_down")
                {
                    interval = root.TryGetProperty("interval", out JsonElement nextInterval) &&
                        nextInterval.TryGetInt32(out int seconds) && seconds > 0
                        ? TimeSpan.FromSeconds(Math.Max(seconds, interval.TotalSeconds + 5))
                        : interval.Add(TimeSpan.FromSeconds(5));
                    continue;
                }

                throw DeviceFlowException.For(error);
            }

            GitHubTokenSession session = ReadToken(root);
            if (string.IsNullOrWhiteSpace(session.RefreshToken) ||
                session.AccessTokenExpiresAt is null || session.RefreshTokenExpiresAt is null)
            {
                throw new InvalidOperationException("GitHub did not return an expiring token and refresh token.");
            }
            return session;
        }

        throw DeviceFlowException.For("expired_token");
    }

    public async Task<GitHubTokenSession> RefreshAsync(
        string clientId,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        using JsonDocument response = await PostAsync(TokenEndpoint,
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token"
            }, cancellationToken).ConfigureAwait(false);
        ThrowIfError(response.RootElement);
        GitHubTokenSession session = ReadToken(response.RootElement);
        if (string.IsNullOrWhiteSpace(session.RefreshToken) ||
            session.AccessTokenExpiresAt is null || session.RefreshTokenExpiresAt is null)
        {
            throw new InvalidOperationException("GitHub did not return a complete replacement token pair.");
        }

        return session;
    }

    private async Task<JsonDocument> PostAsync(
        Uri endpoint,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested && exception.InnerException is TimeoutException)
        {
            throw new HttpRequestException("The GitHub request timed out.", exception);
        }
        using (response)
        {
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private GitHubTokenSession ReadToken(JsonElement root)
    {
        string accessToken = RequiredString(root, "access_token");
        string? refreshToken = OptionalString(root, "refresh_token");
        DateTimeOffset now = _clock.GetUtcNow();
        return new GitHubTokenSession(
            accessToken,
            refreshToken,
            OptionalExpiry(root, "expires_in", now),
            OptionalExpiry(root, "refresh_token_expires_in", now));
    }

    private static DateTimeOffset? OptionalExpiry(JsonElement root, string name, DateTimeOffset now) =>
        root.TryGetProperty(name, out JsonElement value) &&
        value.TryGetInt32(out int seconds) && seconds > 0
            ? now.AddSeconds(seconds)
            : null;

    private static string RequiredString(JsonElement root, string name) =>
        OptionalString(root, name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"GitHub response did not include {name}.");

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int RequiredPositiveInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) &&
        value.TryGetInt32(out int number) && number > 0
            ? number
            : throw new InvalidOperationException($"GitHub response did not include a valid {name}.");

    private static void ThrowIfError(JsonElement root)
    {
        if (OptionalString(root, "error") is { } error)
        {
            throw DeviceFlowException.For(error);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}

public sealed class DeviceFlowException : Exception
{
    private DeviceFlowException(string code, string message) : base(message) => Code = code;

    public string Code { get; }

    internal static DeviceFlowException For(string? code) => code switch
    {
        "access_denied" => new(code, "GitHub authorization was cancelled."),
        "expired_token" or "token_expired" => new("expired_token", "The GitHub sign-in code expired. Try again."),
        "device_flow_disabled" => new(code, "GitHub device sign-in is not enabled for this app."),
        "insufficient_scope" => new(code, "GitHub did not grant the permissions JitHub needs."),
        "incorrect_client_credentials" => new(code, "GitHub rejected this app's client ID."),
        _ => new(code ?? "unknown", "GitHub could not complete sign-in. Try again.")
    };
}
