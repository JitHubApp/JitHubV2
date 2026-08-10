using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using IPNetwork = System.Net.IPNetwork;

namespace JitHub.Web.Services;

public sealed class ForwardedHeaderTrustPolicy
{
    public const string ConfigurationSectionName = "ForwardedHeaders";

    private ForwardedHeaderTrustPolicy(
        IReadOnlyList<IPAddress> knownProxies,
        IReadOnlyList<IPNetwork> knownNetworks)
    {
        KnownProxies = knownProxies;
        KnownNetworks = knownNetworks;
    }

    public IReadOnlyList<IPAddress> KnownProxies { get; }

    public IReadOnlyList<IPNetwork> KnownNetworks { get; }

    public bool IsEnabled => KnownProxies.Count > 0 || KnownNetworks.Count > 0;

    public static ForwardedHeaderTrustPolicy Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(ConfigurationSectionName);
        IReadOnlyList<IPAddress> knownProxies = ParseAddresses(
            section.GetSection("KnownProxies").GetChildren().Select(child => child.Value),
            $"{ConfigurationSectionName}:KnownProxies");
        IReadOnlyList<IPNetwork> knownNetworks = ParseNetworks(
            section.GetSection("KnownNetworks").GetChildren().Select(child => child.Value),
            $"{ConfigurationSectionName}:KnownNetworks");

        return new ForwardedHeaderTrustPolicy(knownProxies, knownNetworks);
    }

    public void Apply(ForwardedHeadersOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (IPAddress proxy in KnownProxies)
        {
            options.KnownProxies.Add(proxy);
        }

        foreach (IPNetwork network in KnownNetworks)
        {
            options.KnownIPNetworks.Add(network);
        }
    }

    private static IReadOnlyList<IPAddress> ParseAddresses(
        IEnumerable<string?> configuredValues,
        string configurationPath)
    {
        List<IPAddress> addresses = [];
        foreach ((string? configuredValue, int index) in configuredValues.Select((value, index) => (value, index)))
        {
            string value = configuredValue?.Trim() ?? string.Empty;
            if (value.Length == 0 || !IPAddress.TryParse(value, out IPAddress? address))
            {
                throw InvalidEntry(configurationPath, index, configuredValue, "an exact IPv4 or IPv6 address");
            }

            if (!addresses.Contains(address))
            {
                addresses.Add(address);
            }
        }

        return addresses;
    }

    private static IReadOnlyList<IPNetwork> ParseNetworks(
        IEnumerable<string?> configuredValues,
        string configurationPath)
    {
        List<IPNetwork> networks = [];
        foreach ((string? configuredValue, int index) in configuredValues.Select((value, index) => (value, index)))
        {
            string value = configuredValue?.Trim() ?? string.Empty;
            if (value.Length == 0 || !IPNetwork.TryParse(value, out IPNetwork network))
            {
                throw InvalidEntry(configurationPath, index, configuredValue, "an IPv4 or IPv6 CIDR network");
            }

            if (!networks.Contains(network))
            {
                networks.Add(network);
            }
        }

        return networks;
    }

    private static InvalidOperationException InvalidEntry(
        string configurationPath,
        int index,
        string? configuredValue,
        string expectedFormat)
    {
        string displayValue = configuredValue is null ? "<null>" : $"'{configuredValue}'";
        return new InvalidOperationException(
            $"Configuration value {configurationPath}:{index} is {displayValue}; expected {expectedFormat}. " +
            "Forwarded headers were not enabled.");
    }
}
