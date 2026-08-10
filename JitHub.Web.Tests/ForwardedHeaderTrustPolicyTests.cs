using System.Net;
using JitHub.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JitHub.Web.Tests;

public sealed class ForwardedHeaderTrustPolicyTests
{
    [Fact]
    public async Task DirectClientCannotSpoofForwardedAddress()
    {
        ForwardedHeaderTrustPolicy policy = LoadPolicy(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:KnownProxies:0"] = "192.0.2.10"
        });
        DefaultHttpContext context = CreateContext(
            remoteAddress: "198.51.100.42",
            forwardedFor: "203.0.113.9");

        await InvokeMiddlewareAsync(policy, context);

        Assert.Equal(IPAddress.Parse("198.51.100.42"), context.Connection.RemoteIpAddress);
        Assert.Equal("http", context.Request.Scheme);
    }

    [Fact]
    public async Task ConfiguredExactProxyCanForwardCallerIdentity()
    {
        ForwardedHeaderTrustPolicy policy = LoadPolicy(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:KnownProxies:0"] = "192.0.2.10"
        });
        DefaultHttpContext context = CreateContext(
            remoteAddress: "192.0.2.10",
            forwardedFor: "203.0.113.9");

        await InvokeMiddlewareAsync(policy, context);

        Assert.Equal(IPAddress.Parse("203.0.113.9"), context.Connection.RemoteIpAddress);
        Assert.Equal("https", context.Request.Scheme);
    }

    [Fact]
    public async Task ConfiguredNetworkCanForwardCallerIdentity()
    {
        ForwardedHeaderTrustPolicy policy = LoadPolicy(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:KnownNetworks:0"] = "192.0.2.0/28"
        });
        DefaultHttpContext context = CreateContext(
            remoteAddress: "192.0.2.7",
            forwardedFor: "203.0.113.19");

        await InvokeMiddlewareAsync(policy, context);

        Assert.Equal(IPAddress.Parse("203.0.113.19"), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public void EmptyConfigurationDisablesForwardedHeaders()
    {
        ForwardedHeaderTrustPolicy policy = LoadPolicy(new Dictionary<string, string?>());

        Assert.False(policy.IsEnabled);
        Assert.Empty(policy.KnownProxies);
        Assert.Empty(policy.KnownNetworks);
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies:0", "10.0.0.0/8")]
    [InlineData("ForwardedHeaders:KnownProxies:0", "not-an-ip")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.0")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/99")]
    public void MalformedConfigurationFailsClosed(string key, string value)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            LoadPolicy(new Dictionary<string, string?> { [key] = value }));

        Assert.Contains(key[..key.LastIndexOf(':')], exception.Message, StringComparison.Ordinal);
        Assert.Contains("Forwarded headers were not enabled", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PolicyDoesNotAddImplicitPrivateNetworks()
    {
        ForwardedHeaderTrustPolicy policy = LoadPolicy(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:KnownProxies:0"] = "192.0.2.10"
        });
        ForwardedHeadersOptions options = new();

        policy.Apply(options);

        Assert.Equal([IPAddress.Parse("192.0.2.10")], options.KnownProxies);
        Assert.Empty(options.KnownIPNetworks);
        Assert.Equal(1, options.ForwardLimit);
    }

    private static ForwardedHeaderTrustPolicy LoadPolicy(IReadOnlyDictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        return ForwardedHeaderTrustPolicy.Load(configuration);
    }

    private static DefaultHttpContext CreateContext(
        string remoteAddress,
        string forwardedFor)
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        return context;
    }

    private static Task InvokeMiddlewareAsync(
        ForwardedHeaderTrustPolicy policy,
        HttpContext context)
    {
        ForwardedHeadersOptions options = new();
        policy.Apply(options);
        ForwardedHeadersMiddleware middleware = new(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(options));
        return middleware.Invoke(context);
    }
}
