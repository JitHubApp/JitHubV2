using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class OAuthHandoffDeploymentContractTests
{
    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void ProductionHandoffUsesEncryptedAtomicDistributedConsumption()
    {
        string root = FindRepositoryRoot();
        string program = File.ReadAllText(Path.Combine(root, "JitHub.Web", "Program.cs"));
        string backend = File.ReadAllText(Path.Combine(
            root,
            "JitHub.Web",
            "Services",
            "RedisOAuthHandoffBackend.cs"));
        XDocument project = XDocument.Load(Path.Combine(root, "JitHub.Web", "JitHub.Web.csproj"));

        Assert.Contains("ConnectionStrings:OAuthHandoffRedis", program, StringComparison.Ordinal);
        Assert.Contains("OAuthHandoff:EncryptionKey", program, StringComparison.Ordinal);
        Assert.Contains("if (!hasRedis && !hasEncryptionKey && isDevelopment)", program, StringComparison.Ordinal);
        Assert.Contains("throw new InvalidOperationException", program, StringComparison.Ordinal);
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
