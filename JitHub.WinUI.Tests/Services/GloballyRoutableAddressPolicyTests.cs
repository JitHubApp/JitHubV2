using System.Net;
using JitHub.Services.Markdown;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GloballyRoutableAddressPolicyTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    public void PublicUnicast_IsAllowed(string value) =>
        Assert.True(GloballyRoutableAddressPolicy.IsGloballyRoutable(IPAddress.Parse(value)));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.0.0.10")]
    [InlineData("192.0.2.10")]
    [InlineData("192.31.196.10")]
    [InlineData("192.52.193.10")]
    [InlineData("192.88.99.1")]
    [InlineData("192.168.1.1")]
    [InlineData("192.175.48.10")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.10")]
    [InlineData("203.0.113.10")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::1")]
    [InlineData("100::1")]
    [InlineData("2001::1")]
    [InlineData("2001:2::1")]
    [InlineData("2001:3::1")]
    [InlineData("2001:4:112::1")]
    [InlineData("2001:10::1")]
    [InlineData("2001:20::1")]
    [InlineData("2001:30::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002::1")]
    [InlineData("2620:4f:8000::1")]
    [InlineData("3fff::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    public void SpecialUseAddresses_AreRejected(string value) =>
        Assert.False(GloballyRoutableAddressPolicy.IsGloballyRoutable(IPAddress.Parse(value)));

    [Theory]
    [InlineData("localhost")]
    [InlineData("service.local")]
    [InlineData("router.internal")]
    [InlineData("service.home.arpa")]
    [InlineData("example.test")]
    [InlineData("example.invalid")]
    [InlineData("service.example")]
    public void SpecialUseHostNames_AreRejected(string host) =>
        Assert.True(GloballyRoutableAddressPolicy.IsSpecialUseHost(host));
}
