using System.Text.Json;
using System.Text.Json.Serialization;

namespace JitHub.Web.Services;

internal sealed class GithubAuthService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GithubAuthService> _logger;

    public GithubAuthService(HttpClient httpClient, ILogger<GithubAuthService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> ExchangeCodeForTokenAsync(string? code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Missing temporary code.");
        }

        string clientId = GetRequiredEnvironmentVariable(
            "JitHubClientId",
            "Missing client information.",
            "JithubClientId");
        string appSecret = GetRequiredEnvironmentVariable("JithubAppSecret", "Missing app secret.");

        using HttpRequestMessage request = new(HttpMethod.Post, "login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = appSecret,
                ["code"] = code
            })
        };

        GitHubTokenResponse? token;

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub OAuth token exchange returned HTTP {StatusCode}.", response.StatusCode);
                throw new InvalidOperationException("GitHub request error.");
            }

            await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            token = await JsonSerializer.DeserializeAsync<GitHubTokenResponse>(responseStream, cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GitHub OAuth token exchange failed.");
            throw new InvalidOperationException("GitHub request error.", ex);
        }

        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken) || !string.IsNullOrWhiteSpace(token.Error))
        {
            _logger.LogWarning(
                "GitHub OAuth token response was invalid. Error: {Error}; Description: {Description}",
                token?.Error,
                token?.ErrorDescription);
            throw new InvalidOperationException("GitHub returned missing token information.");
        }

        return token.AccessToken;
    }

    private static string GetRequiredEnvironmentVariable(string name, string errorMessage, params string[] fallbackNames)
    {
        foreach (string candidate in EnumerateEnvironmentVariableNames(name, fallbackNames))
        {
            string? value = Environment.GetEnvironmentVariable(candidate);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        throw new InvalidOperationException(errorMessage);
    }

    private static IEnumerable<string> EnumerateEnvironmentVariableNames(string primaryName, IEnumerable<string> fallbackNames)
    {
        yield return primaryName;

        foreach (string fallbackName in fallbackNames)
        {
            if (!string.Equals(fallbackName, primaryName, StringComparison.Ordinal))
            {
                yield return fallbackName;
            }
        }
    }

    private sealed class GitHubTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }
}
