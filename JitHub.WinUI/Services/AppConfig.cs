using System;
using JitHub.Models;
using Microsoft.Extensions.Configuration;

namespace JitHub.Services;

public class AppConfig : IAppConfig
{
    private const string OAuthClientIdEnvironmentVariable = "JITHUB_OAUTH_CLIENT_ID";
    private const string OAuthCallbackUrlEnvironmentVariable = "JITHUB_OAUTH_CALLBACK_URL";

    private readonly IConfigurationRoot _configurationRoot;

    public AppConfig()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false);

        _configurationRoot = builder.Build();
    }

    public Credential Credential
    {
        get
        {
            Credential credential = _configurationRoot.GetSection(nameof(Credential)).Get<Credential>() ?? new Credential();
            bool useDevelopmentCredential = ShouldUseDevelopmentCredential();
            string? environmentClientId = Environment.GetEnvironmentVariable(OAuthClientIdEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(environmentClientId))
            {
                credential.ClientId = environmentClientId.Trim();
            }
            else if (useDevelopmentCredential && !string.IsNullOrWhiteSpace(credential.DevelopmentClientId))
            {
                credential.ClientId = credential.DevelopmentClientId.Trim();
            }

            string? environmentCallbackUrl = Environment.GetEnvironmentVariable(OAuthCallbackUrlEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(environmentCallbackUrl))
            {
                credential.AuthorizationCallbackUrl = environmentCallbackUrl.Trim();
            }
            else if (useDevelopmentCredential &&
                !string.IsNullOrWhiteSpace(credential.DevelopmentAuthorizationCallbackUrl))
            {
                credential.AuthorizationCallbackUrl = credential.DevelopmentAuthorizationCallbackUrl.Trim();
            }

            return credential;
        }
    }

    private static bool ShouldUseDevelopmentCredential()
    {
#if DEBUG
        return true;
#else
        return string.Equals(
            Environment.GetEnvironmentVariable("JITHUB_USE_DEV_OAUTH_APP"),
            "true",
            StringComparison.OrdinalIgnoreCase);
#endif
    }
}
