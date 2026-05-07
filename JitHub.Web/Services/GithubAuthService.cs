using System.Text.Json;
using System.Text.Json.Serialization;

namespace JitHub.Web.Services;

internal sealed class GithubAuthService
{
    private const string DevelopmentClientId = "Ov23libqduSlPx5TcCne";
    private const string UseProductionOAuthInDevelopmentSetting = "JITHUB_WEB_USE_PRODUCTION_OAUTH";

    private readonly HttpClient _httpClient;
    private readonly ILogger<GithubAuthService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    public GithubAuthService(
        HttpClient httpClient,
        ILogger<GithubAuthService> logger,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
        _environment = environment;
    }

    public async Task<string> ExchangeCodeForTokenAsync(
        string? code,
        string? redirectUri,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Missing temporary code.");
        }

        OAuthApplication oauthApplication = GetOAuthApplication();

        Dictionary<string, string> tokenRequestFields = new()
        {
            ["client_id"] = oauthApplication.ClientId,
            ["client_secret"] = oauthApplication.ClientSecret,
            ["code"] = code
        };

        if (TryNormalizeRedirectUri(redirectUri, out string normalizedRedirectUri))
        {
            tokenRequestFields["redirect_uri"] = normalizedRedirectUri;
            _logger.LogInformation("Exchanging GitHub OAuth code with redirect URI {RedirectUri}.", normalizedRedirectUri);
        }
        else
        {
            _logger.LogWarning("Exchanging GitHub OAuth code without a redirect URI.");
        }

        using HttpRequestMessage request = new(HttpMethod.Post, "login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(tokenRequestFields)
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

        if (token is not null && !string.IsNullOrWhiteSpace(token.Error))
        {
            string error = token.Error;
            string? errorDescription = token.ErrorDescription;
            _logger.LogWarning(
                "GitHub OAuth token response was invalid. Error: {Error}; Description: {Description}",
                error,
                errorDescription);
            string detail = string.IsNullOrWhiteSpace(errorDescription)
                ? error
                : errorDescription;
            throw new InvalidOperationException($"GitHub rejected the OAuth code: {detail}");
        }

        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            _logger.LogWarning("GitHub OAuth token response was missing an access token.");
            throw new InvalidOperationException("GitHub returned missing token information.");
        }

        return token.AccessToken;
    }

    private OAuthApplication GetOAuthApplication()
    {
        if (ShouldUseDevelopmentOAuthApplication())
        {
            string clientId = GetSetting(
                    "JITHUB_DEV_OAUTH_CLIENT_ID",
                    "GitHubOAuth:DevelopmentClientId",
                    "GitHubOAuth:DevClientId")
                ?? DevelopmentClientId;
            string developmentClientSecret = GetRequiredSetting(
                "JITHUB_DEV_OAUTH_CLIENT_SECRET",
                "Missing development app secret.",
                "JitHubDevelopmentAppSecret",
                "JithubDevelopmentAppSecret",
                "GitHubOAuth:DevelopmentClientSecret",
                "GitHubOAuth:DevClientSecret");
            _logger.LogInformation("Using the development GitHub OAuth app for local token exchange.");
            return new OAuthApplication(clientId, developmentClientSecret);
        }

        string configuredClientId = GetRequiredSetting(
            "JitHubClientId",
            "Missing client information.",
            "JithubClientId",
            "GitHubOAuth:ClientId");
        string productionClientSecret = GetRequiredSetting(
            "JithubAppSecret",
            "Missing app secret.",
            "GitHubOAuth:ClientSecret");
        return new OAuthApplication(configuredClientId, productionClientSecret);
    }

    private bool ShouldUseDevelopmentOAuthApplication()
    {
        if (!_environment.IsDevelopment())
        {
            return false;
        }

        string? useProductionOAuth = GetSetting(UseProductionOAuthInDevelopmentSetting);
        return !string.Equals(useProductionOAuth, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeRedirectUri(string? redirectUri, out string normalizedRedirectUri)
    {
        normalizedRedirectUri = string.Empty;
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            return false;
        }

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalizedRedirectUri = uri.GetLeftPart(UriPartial.Path);
        return true;
    }

    private string GetRequiredSetting(string name, string errorMessage, params string[] fallbackNames)
    {
        string? value = GetSetting(name, fallbackNames);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException(errorMessage);
    }

    private string? GetSetting(string primaryName, params string[] fallbackNames)
    {
        foreach (string candidate in EnumerateSettingNames(primaryName, fallbackNames))
        {
            string? value = Environment.GetEnvironmentVariable(candidate) ?? _configuration[candidate];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSettingNames(string primaryName, IEnumerable<string> fallbackNames)
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

    private sealed record OAuthApplication(string ClientId, string ClientSecret);
}
