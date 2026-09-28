using System.Text.Json;
using Xunit;

namespace JitHub.Web.Tests;

public sealed class WebsiteDeploymentContractTests
{
    [Fact]
    public void LegacyWebsiteStillChecksProductionStartupWithoutDeploying()
    {
        string root = FindRepositoryRoot();
        string workflow = File.ReadAllText(
            Path.Combine(root, ".github", "workflows", "main_jithubweb.yml"));

        Assert.Contains("Smoke test production startup", workflow, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_ENVIRONMENT=Production", workflow, StringComparison.Ordinal);
        Assert.Contains("WEBSITE_HOSTNAME=jithub-web-prod.azurewebsites.net", workflow, StringComparison.Ordinal);
        Assert.Contains("/healthz", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("azure/webapps-deploy", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("JITHUB_WEBAPP_PUBLISH_PROFILE", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedAppUsesDeviceSignInWithoutACallback()
    {
        string root = FindRepositoryRoot();
        using JsonDocument settings = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "appsettings.json")));
        Assert.False(settings.RootElement.GetProperty("Credential")
            .TryGetProperty("AuthorizationCallbackUrl", out _));
        Assert.Equal("jithub.app", File.ReadAllText(
            Path.Combine(root, "JitHub.Site", "public", "CNAME")).Trim());
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
