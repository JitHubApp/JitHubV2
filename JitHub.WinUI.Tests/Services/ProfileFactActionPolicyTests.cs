using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ProfileFactActionPolicyTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("https://example.com/about", "https://example.com/about")]
    public void WebsiteUsesSafeHttpsLaunchPolicy(string value, string expected)
    {
        ProfileFactAction action = Assert.IsType<ProfileFactAction>(
            ProfileFactActionPolicy.CreateWebsite(value));

        Assert.Equal(expected, action.LaunchUri.AbsoluteUri);
        Assert.Equal(value, action.CopyValue);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://example.com")]
    [InlineData("file:///c:/secret")]
    public void WebsiteRejectsUnsafeSchemes(string value) =>
        Assert.Null(ProfileFactActionPolicy.CreateWebsite(value));

    [Fact]
    public void EmailCreatesMailtoAndPreservesCopyValue()
    {
        ProfileFactAction action = Assert.IsType<ProfileFactAction>(
            ProfileFactActionPolicy.CreateEmail("person@example.com"));

        Assert.Equal("mailto:person@example.com", action.LaunchUri.AbsoluteUri);
        Assert.Equal("person@example.com", action.CopyValue);
    }

    [Theory]
    [InlineData("person@example.com\r\nsubject=unsafe")]
    [InlineData("not an email")]
    [InlineData("two@@example.com")]
    public void EmailRejectsMalformedValues(string value) =>
        Assert.Null(ProfileFactActionPolicy.CreateEmail(value));

    [Theory]
    [InlineData("@jithub", "https://x.com/jithub")]
    [InlineData("jithub_dev", "https://x.com/jithub_dev")]
    public void TwitterCreatesSafeProfileUri(string value, string expected)
    {
        ProfileFactAction action = Assert.IsType<ProfileFactAction>(
            ProfileFactActionPolicy.CreateTwitter(value));

        Assert.Equal(expected, action.LaunchUri.AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("far-too-long-twitter-username")]
    public void TwitterRejectsInvalidUsernames(string value) =>
        Assert.Null(ProfileFactActionPolicy.CreateTwitter(value));
}
