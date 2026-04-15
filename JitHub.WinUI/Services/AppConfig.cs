using System;
using JitHub.Models;
using Microsoft.Extensions.Configuration;

namespace JitHub.Services;

public class AppConfig : IAppConfig
{
    private readonly IConfigurationRoot _configurationRoot;

    public AppConfig()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false);

        _configurationRoot = builder.Build();
    }

    public Credential Credential => _configurationRoot.GetSection(nameof(Credential)).Get<Credential>() ?? new Credential();
}
