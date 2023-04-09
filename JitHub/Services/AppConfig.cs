using JitHub.Models;
using Microsoft.Extensions.Configuration;
using Windows.ApplicationModel;

namespace JitHub.Services
{
    public class AppConfig : IAppConfig
    {
        private readonly IConfigurationRoot _configurationRoot;
        private T GetSection<T>(string key) => _configurationRoot.GetSection(key).Get<T>();

        public Credential Credential => GetSection<Credential>(nameof(Credential));

        public AppConfig()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Package.Current.InstalledLocation.Path)
                .AddJsonFile("appsettings.json", optional: false);

            _configurationRoot = builder.Build();
        }
        
    }
}
