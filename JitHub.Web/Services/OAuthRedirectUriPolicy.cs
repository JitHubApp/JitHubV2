namespace JitHub.Web.Services;

internal sealed class OAuthRedirectUriPolicy
{
    internal const string CallbackUrlSetting = "GitHubOAuth:AuthorizationCallbackUrl";
    internal const string CallbackUrlEnvironmentSetting = "JITHUB_OAUTH_CALLBACK_URL";
    internal const string DevelopmentCallbackUrlsSection = "GitHubOAuth:DevelopmentCallbackUrls";

    private static readonly string[] DefaultDevelopmentCallbacks =
    [
        "https://localhost:7284/authorize",
        "http://localhost:5280/authorize",
        "https://localhost:44396/authorize"
    ];

    private readonly HashSet<string> _allowedRedirectUris;

    private OAuthRedirectUriPolicy(HashSet<string> allowedRedirectUris)
    {
        _allowedRedirectUris = allowedRedirectUris;
    }

    public static OAuthRedirectUriPolicy Load(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        HashSet<string> allowedRedirectUris = new(StringComparer.Ordinal);
        string? configuredCallback = GetConfiguredCallback(configuration);
        if (!string.IsNullOrWhiteSpace(configuredCallback))
        {
            allowedRedirectUris.Add(NormalizeConfiguredCallback(configuredCallback, environment.IsDevelopment()));
        }
        else if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"Production OAuth requires {CallbackUrlEnvironmentSetting} or {CallbackUrlSetting}.");
        }

        if (environment.IsDevelopment())
        {
            foreach (string callback in DefaultDevelopmentCallbacks)
            {
                allowedRedirectUris.Add(NormalizeDevelopmentLoopbackCallback(callback));
            }

            foreach (IConfigurationSection callbackSection in configuration.GetSection(DevelopmentCallbackUrlsSection).GetChildren())
            {
                if (!string.IsNullOrWhiteSpace(callbackSection.Value))
                {
                    allowedRedirectUris.Add(NormalizeDevelopmentLoopbackCallback(callbackSection.Value));
                }
            }
        }

        return new OAuthRedirectUriPolicy(allowedRedirectUris);
    }

    public string RequireAllowed(string? redirectUri)
    {
        if (!TryNormalizeCallback(redirectUri, out string normalizedRedirectUri) ||
            !_allowedRedirectUris.Contains(normalizedRedirectUri))
        {
            throw new InvalidOperationException("The OAuth redirect URI is not allowed.");
        }

        return normalizedRedirectUri;
    }

    internal IReadOnlyCollection<string> AllowedRedirectUris => _allowedRedirectUris;

    private static string? GetConfiguredCallback(IConfiguration configuration) =>
        configuration[CallbackUrlEnvironmentSetting] ??
        configuration[CallbackUrlSetting] ??
        configuration["GitHubOAuth:CallbackUrl"];

    private static string NormalizeConfiguredCallback(string callback, bool isDevelopment)
    {
        if (!TryNormalizeCallback(callback, out string normalizedCallback))
        {
            throw new InvalidOperationException($"{CallbackUrlSetting} must be an absolute callback URL without query or fragment data.");
        }

        Uri uri = new(normalizedCallback, UriKind.Absolute);
        if (!isDevelopment && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{CallbackUrlSetting} must use HTTPS in production.");
        }

        return normalizedCallback;
    }

    private static string NormalizeDevelopmentLoopbackCallback(string callback)
    {
        if (!TryNormalizeCallback(callback, out string normalizedCallback))
        {
            throw new InvalidOperationException(
                $"{DevelopmentCallbackUrlsSection} entries must be absolute callback URLs without query or fragment data.");
        }

        Uri uri = new(normalizedCallback, UriKind.Absolute);
        if (!uri.IsLoopback)
        {
            throw new InvalidOperationException($"{DevelopmentCallbackUrlsSection} entries must use a loopback host.");
        }

        return normalizedCallback;
    }

    private static bool TryNormalizeCallback(string? redirectUri, out string normalizedRedirectUri)
    {
        normalizedRedirectUri = string.Empty;
        if (string.IsNullOrWhiteSpace(redirectUri) ||
            !Uri.TryCreate(redirectUri.Trim(), UriKind.Absolute, out Uri? uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (!string.Equals(uri.AbsolutePath, "/authorize", StringComparison.Ordinal))
        {
            return false;
        }

        UriBuilder builder = new(uri)
        {
            Path = "/authorize",
            Query = string.Empty,
            Fragment = string.Empty
        };
        normalizedRedirectUri = builder.Uri.GetLeftPart(UriPartial.Path);
        return true;
    }
}
