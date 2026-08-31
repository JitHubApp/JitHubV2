using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class UserIdentityNavigationPolicyTests
{
    [Theory]
    [InlineData("octocat")]
    [InlineData("valid-user")]
    public void RealUsersAreRoutable(string login) =>
        Assert.True(UserIdentityNavigationPolicy.CanNavigate(login));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ghost")]
    [InlineData("deleted")]
    [InlineData("[deleted]")]
    [InlineData("unknown")]
    [InlineData("Unknown User")]
    [InlineData("someone")]
    [InlineData("somebody")]
    [InlineData("anonymous")]
    [InlineData("unavailable")]
    [InlineData("a user")]
    [InlineData("Display Name")]
    [InlineData("name@example.com")]
    [InlineData("-invalid")]
    [InlineData("invalid-")]
    [InlineData("dependabot[bot]")]
    public void UnavailableAndBotIdentitiesAreNotRoutable(string? login) =>
        Assert.False(UserIdentityNavigationPolicy.CanNavigate(login));

    [Fact]
    public void RoutableLoginIsTrimmedAndFallbackSentinelsBecomeNull()
    {
        Assert.Equal("octocat", UserIdentityNavigationPolicy.GetRoutableLogin("  octocat  "));
        Assert.Null(UserIdentityNavigationPolicy.GetRoutableLogin("someone"));
        Assert.Null(UserIdentityNavigationPolicy.GetRoutableLogin("renovate[bot]"));
    }
}
