using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class OAuthHandoffDeploymentContractTests
{
    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void ProductionHandoffPrefersEncryptedAtomicDistributedConsumption()
    {
        string root = FindRepositoryRoot();
        string registration = File.ReadAllText(Path.Combine(
            root,
            "JitHub.Web",
            "Services",
            "OAuthHandoffBackendRegistration.cs"));
        string backend = File.ReadAllText(Path.Combine(
            root,
            "JitHub.Web",
            "Services",
            "RedisOAuthHandoffBackend.cs"));
        XDocument project = XDocument.Load(Path.Combine(root, "JitHub.Web", "JitHub.Web.csproj"));

        Assert.Contains("GetConnectionString(RedisConnectionStringName)", registration, StringComparison.Ordinal);
        Assert.Contains("OAuthHandoff:EncryptionKey", registration, StringComparison.Ordinal);
        Assert.Contains("ConfigurationOptions.Parse", registration, StringComparison.Ordinal);
        Assert.Contains("RedisOAuthHandoffBackend", registration, StringComparison.Ordinal);
        Assert.Contains("StringGetDeleteAsync", backend, StringComparison.Ordinal);
        Assert.Contains("When.NotExists", backend, StringComparison.Ordinal);
        Assert.Contains("new AesGcm", backend, StringComparison.Ordinal);
        Assert.Contains("RandomNumberGenerator.GetBytes", backend, StringComparison.Ordinal);
        Assert.Contains(
            project.Descendants("PackageReference"),
            element => string.Equals(
                (string?)element.Attribute("Include"),
                "Microsoft.Extensions.Caching.StackExchangeRedis",
                StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void UnconfiguredHandoffFallbackRemainsBoundedShortLivedAndAtomic()
    {
        string root = FindRepositoryRoot();
        string registration = File.ReadAllText(Path.Combine(
            root,
            "JitHub.Web",
            "Services",
            "OAuthHandoffBackendRegistration.cs"));
        string store = File.ReadAllText(Path.Combine(
            root,
            "JitHub.Web",
            "Services",
            "OAuthHandoffStore.cs"));

        Assert.Contains("if (!hasRedis || !hasEncryptionKey)", registration, StringComparison.Ordinal);
        Assert.Contains("UseInMemoryBackend", registration, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMinutes(2)", store, StringComparison.Ordinal);
        Assert.Contains("MaximumPendingHandoffs = 10_000", store, StringComparison.Ordinal);
        Assert.Contains("_entries.TryRemove", store, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "JitHub.Web")) &&
                Directory.Exists(Path.Combine(current.FullName, "JitHub.WinUI")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
