using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class OAuthProtocolIdentityContractTests
{
    private const string AppxNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    [Fact]
    public void DebugAndReleaseManifestsDoNotRegisterAuthenticationProtocols()
    {
        ManifestIdentity release = ReadManifest("JitHub.WinUI", "Package.appxmanifest");
        ManifestIdentity debug = ReadManifest("JitHub.WinUI", "Package.Debug.appxmanifest");

        Assert.Equal("54742Neromarah.JitHub", release.PackageName);
        Assert.Equal("JitHub.WinUI.Debug", debug.PackageName);
        Assert.NotEqual(release.PackageName, debug.PackageName);
        Assert.False(release.HasProtocol);
        Assert.False(debug.HasProtocol);
    }

    [Fact]
    public void ProjectSelectsManifestByConfiguration()
    {
        XDocument project = XDocument.Load(Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "JitHub.WinUI.csproj"));
        XElement[] manifests = project.Descendants("AppxManifest").ToArray();

        XElement debug = Assert.Single(manifests, element => (string?)element.Attribute("Include") == "Package.Debug.appxmanifest");
        XElement release = Assert.Single(manifests, element => (string?)element.Attribute("Include") == "Package.appxmanifest");

        Assert.Contains("$(Configuration)' == 'Debug", (string?)debug.Attribute("Condition"), StringComparison.Ordinal);
        Assert.Contains("$(Configuration)' != 'Debug", (string?)release.Attribute("Condition"), StringComparison.Ordinal);
    }

    [Fact]
    public void HostedCallbackMapsDebugStateToDebugScheme()
    {
        string script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "JitHub.Web", "wwwroot", "js", "authorize.js"));

        Assert.Contains("WINUI3V3DEBUG_", script, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("access_token", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("handoff=", script, StringComparison.Ordinal);
        Assert.Contains("? \"jithub-dev\"", script, StringComparison.Ordinal);
        Assert.Contains(": \"jithub\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WebHostExposesOnlyServerSideHandoffCreationAndRedemption()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "JitHub.Web", "Program.cs"));

        Assert.Contains("api.MapPost(\"/GithubCodeToHandoff\"", source, StringComparison.Ordinal);
        Assert.Contains("api.MapPost(\"/RedeemGithubHandoff\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapGet(\"/GithubCodeToToken\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("redirectUri + \"?token=\"", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DesktopConfigurationDoesNotIncludeCallbackAddresses()
    {
        string configuration = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "appsettings.json"));
        string activation = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "App.xaml.cs"));
        Assert.DoesNotContain("AuthorizationCallbackUrl", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorize(authResponse)", activation, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetAuthProtocolActivationResponse", activation, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugLauncherKeepsDedicatedIdentityAndRunsGuardedCleanup()
    {
        string launcher = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "eng", "Start-JitHubWinUIDebug.ps1"));
        string cleanup = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "eng", "Reset-JitHubWinUIDebugIdentity.ps1"));

        Assert.Contains("--keep-identity", launcher, StringComparison.Ordinal);
        Assert.Contains("Reset-JitHubWinUIDebugIdentity.ps1", launcher, StringComparison.Ordinal);
        Assert.Contains("$currentAssembly", launcher, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $looseLayoutAppHost -Destination $exePath", launcher, StringComparison.Ordinal);
        Assert.Contains("IsDevelopmentMode", cleanup, StringComparison.Ordinal);
        Assert.Contains("JitHub.WinUI.Debug", cleanup, StringComparison.Ordinal);
        Assert.Contains("54742Neromarah.JitHub", cleanup, StringComparison.Ordinal);
        Assert.Contains("$repositoryDirectory", cleanup, StringComparison.Ordinal);
    }

    private static ManifestIdentity ReadManifest(params string[] pathParts)
    {
        XDocument document = XDocument.Load(Path.Combine([FindRepositoryRoot(), .. pathParts]));
        XNamespace appx = AppxNamespace;
        XElement package = Assert.Single(document.Descendants(appx + "Identity"));
        bool hasProtocol = document.Descendants().Any(element => element.Name.LocalName == "Protocol");
        return new((string)package.Attribute("Name")!, hasProtocol);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }

    private sealed record ManifestIdentity(string PackageName, bool HasProtocol);
}
