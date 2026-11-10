using JitHub.Core.Models.Authentication;
using JitHub.Core.Services.Authentication;
using JitHub.Core.Services.Configuration;
using JitHub.Core.Services.Telemetry;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace JitHub.Core.Tests.Services.Telemetry;

public sealed class SecurityReleaseGateTests
{
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
        store.SaveAccountSession(42, new GitHubTokenSession(
            "rotating-access", "rotating-refresh", DateTimeOffset.UtcNow.AddHours(8),
            DateTimeOffset.UtcNow.AddMonths(6), ["repo", "gist"]));
        store.SaveAccountToken(41, "token-one-replaced");

        Assert.Equal("token-one-replaced", store.GetAccountToken(41));
        Assert.Equal("rotating-access", store.GetAccountToken(42));
        Assert.Equal("rotating-refresh", store.GetAccountSession(42)?.RefreshToken);
        Assert.Contains("gist", store.GetAccountSession(42)?.GrantedScopes ?? []);

        store.RemoveAccountToken(41);

        Assert.Null(store.GetAccountToken(41));
        Assert.Equal("rotating-access", store.GetAccountToken(42));
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
}
