using JitHub.Models;
using JitHub.Services;
using JitHub.Services.Markdown;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class SecurityReleaseGateTests
{
    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void ReleaseBuild_EnforcesLockedAuditedDependencyPolicy()
    {
        string root = FindRepositoryRoot();
        string props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        string nativeAotProps = File.ReadAllText(Path.Combine(root, "eng", "NativeAot.props"));
        string project = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "JitHub.WinUI.csproj"));
        string script = File.ReadAllText(Path.Combine(root, "eng", "Verify-DependencySecurity.ps1"));

        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", props, StringComparison.Ordinal);
        Assert.Contains("<NuGetLockFilePath>$(MSBuildProjectDirectory)\\obj\\$(Configuration)\\packages.lock.json</NuGetLockFilePath>", props, StringComparison.Ordinal);
        Assert.Contains("'$(Configuration)' != 'Release' and '$(Configuration)' != 'AotDebug'", props, StringComparison.Ordinal);
        Assert.Contains("<RestoreLockedMode>true</RestoreLockedMode>", props, StringComparison.Ordinal);
        Assert.Contains("'$(Configuration)' == 'Release' or '$(Configuration)' == 'AotDebug'", props, StringComparison.Ordinal);
        Assert.Contains("<NuGetLockFilePath>$(MSBuildProjectDirectory)\\obj\\$(Configuration)\\packages.lock.json</NuGetLockFilePath>", nativeAotProps, StringComparison.Ordinal);
        Assert.Contains("<RestoreLockedMode>true</RestoreLockedMode>", nativeAotProps, StringComparison.Ordinal);
        Assert.Contains("NU1901;NU1902;NU1903;NU1904", props, StringComparison.Ordinal);
        Assert.Contains("Verify-DependencySecurity.ps1", project, StringComparison.Ordinal);
        Assert.Contains("--locked-mode", script, StringComparison.Ordinal);
        Assert.Contains("-p:Configuration=Release", script, StringComparison.Ordinal);
        Assert.Contains("--vulnerable", script, StringComparison.Ordinal);
        Assert.Contains("--include-transitive", script, StringComparison.Ordinal);
        Assert.Contains("allowedPrereleasePackages", script, StringComparison.Ordinal);
        Assert.Contains("UriSchemeHttps", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void StorePackage_RestoresNativeGraphBeforeVerifyingDependencyLedger()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(Path.Combine(root, "eng", "Build-JitHubWinUIStorePackage.ps1"));
        int restoreIndex = script.IndexOf(
            "Restore-NativeAot.ps1') -Architecture $ledgerArchitecture",
            StringComparison.Ordinal);
        int verifyIndex = script.IndexOf(
            "Update-NativeAotDependencyLedger.ps1') -Verify",
            StringComparison.Ordinal);

        Assert.True(restoreIndex >= 0, "The Store package script must materialize a locked Native AOT graph.");
        Assert.True(verifyIndex > restoreIndex, "The dependency ledger must be verified after the Native AOT restore.");
        Assert.Contains("-Platform @($platforms)[0]", script, StringComparison.Ordinal);
        int testRestoreIndex = script.IndexOf("& dotnet restore $resolvedTestProjectPath", StringComparison.Ordinal);
        int releaseConfigurationIndex = script.IndexOf("-p:Configuration=Release", testRestoreIndex, StringComparison.Ordinal);
        int lockedModeIndex = script.IndexOf("--locked-mode", testRestoreIndex, StringComparison.Ordinal);
        Assert.True(
            testRestoreIndex >= 0 && releaseConfigurationIndex > testRestoreIndex && lockedModeIndex > releaseConfigurationIndex,
            "The Store package script must verify the canonical Release test lock file.");
    }

    [Theory]
    [Trait("Category", "ReleaseSecurity")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///C:/Users/shared/.ssh/id_rsa")]
    [InlineData("ms-appx:///Assets/secret.txt")]
    [InlineData("ftp://example.test/archive")]
    [InlineData("http://example.test/plaintext")]
    public void MarkdownNavigation_DeniesUnsafeSchemes(string value)
    {
        bool resolved = MarkdownLinkNavigationPolicy.TryResolveLaunchUri(
            value,
            new Uri("https://github.com/JitHubApp/JitHubV2/"),
            out Uri? uri);

        Assert.False(resolved);
        Assert.Null(uri);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void OAuthProtocolPolicy_DeniesUnregisteredAndCrossBuildSchemes()
    {
        Assert.True(AuthProtocolPolicy.IsExpectedScheme(
            new Uri("jithub://auth?state=state"),
            useDevelopmentScheme: false));
        Assert.False(AuthProtocolPolicy.IsExpectedScheme(
            new Uri("jithub-dev://auth?state=state"),
            useDevelopmentScheme: false));
        Assert.True(AuthProtocolPolicy.IsExpectedScheme(
            new Uri("jithub-dev://auth?state=state"),
            useDevelopmentScheme: true));
        Assert.False(AuthProtocolPolicy.IsExpectedScheme(
            new Uri("https://attacker.test/auth?state=state"),
            useDevelopmentScheme: true));
        Assert.False(AuthProtocolPolicy.IsExpectedScheme(
            new Uri("file:///auth?state=state"),
            useDevelopmentScheme: true));
    }

    [Theory]
    [Trait("Category", "ReleaseSecurity")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("github_pat_11AAabcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("token=super-secret-value")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature123")]
    public void TelemetrySanitizer_DropsSecretShapedValues(string secret)
    {
        IReadOnlyDictionary<string, string> sanitized = TelemetrySanitizer.SanitizeProperties(
            new Dictionary<string, string?>
            {
                ["result"] = secret,
                ["source"] = TelemetryTaxonomy.Sources.User
            });

        Assert.Equal(TelemetryTaxonomy.Sources.User, sanitized["source"]);
        Assert.False(sanitized.ContainsKey("result"));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void TelemetrySanitizer_DropsUnknownAndSecretShapedFields()
    {
        IReadOnlyDictionary<string, string> sanitized = TelemetrySanitizer.SanitizeProperties(
            new Dictionary<string, string?>
            {
                ["result"] = "success",
                ["account_name"] = "octocat",
                ["custom_dimension"] = "looks-harmless",
                ["authorization"] = "redacted",
                ["token"] = "redacted"
            });

        Assert.Equal(new[] { "result" }, sanitized.Keys.OrderBy(static key => key).ToArray());
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void CredentialStore_PreservesAccountIsolationAndLifecycle()
    {
        MemoryCredentialVaultBackend backend = new();
        AuthCredentialStore store = new(backend, new TestAppConfig("test-client"));

        store.SaveAccountToken(41, "token-one");
        store.SaveAccountToken(42, "token-two");
        store.SavePendingToken("pending-token");
        store.SavePendingState("pending-state");
        store.SavePendingVerifier("pending-verifier");
        store.SaveAccountToken(41, "token-one-replaced");

        Assert.Equal("token-one-replaced", store.GetAccountToken(41));
        Assert.Equal("token-two", store.GetAccountToken(42));
        Assert.Equal("pending-token", store.GetPendingToken());
        Assert.Equal("pending-state", store.GetPendingState());
        Assert.Equal("pending-verifier", store.GetPendingVerifier());

        store.RemoveAccountToken(41);
        store.RemovePendingToken();
        store.RemovePendingState();
        store.RemovePendingVerifier();

        Assert.Null(store.GetAccountToken(41));
        Assert.Equal("token-two", store.GetAccountToken(42));
        Assert.Null(store.GetPendingToken());
        Assert.Null(store.GetPendingState());
        Assert.Null(store.GetPendingVerifier());
        Assert.DoesNotContain(backend.Values.Keys, key => key.UserName == "41");
    }

    private sealed class TestAppConfig(string clientId) : IAppConfig
    {
        public Credential Credential { get; } = new() { ClientId = clientId };
    }

    private sealed class MemoryCredentialVaultBackend : ICredentialVaultBackend
    {
        public Dictionary<(string Resource, string UserName), string> Values { get; } = [];

        public string? Retrieve(string resource, string userName) =>
            Values.TryGetValue((resource, userName), out string? value) ? value : null;

        public void Store(string resource, string userName, string secret) =>
            Values[(resource, userName)] = secret;

        public void Remove(string resource, string userName) =>
            Values.Remove((resource, userName));
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
