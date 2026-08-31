using JitHub.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JitHub.Web.Tests;

public sealed class OAuthHandoffBackendRegistrationTests
{
    [Fact]
    public void ProductionWithoutRedisUsesBoundedProcessLocalBackend()
    {
        ServiceCollection services = new();
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>());

        OAuthHandoffBackendSelection selection = OAuthHandoffBackendRegistration.Configure(
            services,
            configuration,
            isDevelopment: false);

        Assert.False(selection.UsesRedis);
        Assert.Contains("absent or incomplete", selection.FallbackReason, StringComparison.Ordinal);
        ServiceDescriptor backend = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IOAuthHandoffBackend));
        Assert.Equal(typeof(InMemoryOAuthHandoffBackend), backend.ImplementationType);
    }

    [Fact]
    public void DevelopmentWithoutRedisUsesProcessLocalBackendWithoutWarning()
    {
        ServiceCollection services = new();

        OAuthHandoffBackendSelection selection = OAuthHandoffBackendRegistration.Configure(
            services,
            BuildConfiguration(new Dictionary<string, string?>()),
            isDevelopment: true);

        Assert.False(selection.UsesRedis);
        Assert.Null(selection.FallbackReason);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("c2hvcnQ=")]
    public void InvalidEncryptionKeyFallsBackWithoutCrashingStartup(string encryptionKey)
    {
        ServiceCollection services = new();
        IConfiguration configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{OAuthHandoffBackendRegistration.RedisConnectionStringName}"] =
                    "localhost:6379",
                [OAuthHandoffBackendRegistration.EncryptionKeySetting] = encryptionKey
            });

        OAuthHandoffBackendSelection selection = OAuthHandoffBackendRegistration.Configure(
            services,
            configuration,
            isDevelopment: false);

        Assert.False(selection.UsesRedis);
        Assert.Contains("encryption key", selection.FallbackReason, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteRedisConfigurationSelectsDistributedBackend()
    {
        ServiceCollection services = new();
        IConfiguration configuration = BuildConfiguration(
            new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{OAuthHandoffBackendRegistration.RedisConnectionStringName}"] =
                    "localhost:6379",
                [OAuthHandoffBackendRegistration.EncryptionKeySetting] =
                    Convert.ToBase64String(new byte[32])
            });

        OAuthHandoffBackendSelection selection = OAuthHandoffBackendRegistration.Configure(
            services,
            configuration,
            isDevelopment: false);

        Assert.True(selection.UsesRedis);
        Assert.Null(selection.FallbackReason);
        Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IOAuthHandoffBackend));
    }

    private static IConfiguration BuildConfiguration(
        IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
