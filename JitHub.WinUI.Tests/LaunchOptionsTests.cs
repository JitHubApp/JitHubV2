using Xunit;
using JitHub.Core.Models.Activation;

namespace JitHub.WinUI.Tests;

public sealed class LaunchOptionsTests
{
    [Fact]
    public void Parse_ReadsPackagedLaunchArgumentsAndQuotedValues()
    {
        LaunchOptions options = LaunchOptions.Parse(
            [],
            "--page=repo --theme=dark --palette=github --repo=sindresorhus/awesome " +
            "--scenario=\"C:\\readmes\\awesome README.md\"");

        Assert.Equal("repo", options.Page);
        Assert.Equal("dark", options.Theme);
        Assert.Equal("github", options.Palette);
        Assert.Equal("sindresorhus/awesome", options.RepositoryFullName);
        Assert.Equal("C:\\readmes\\awesome README.md", options.Scenario);
    }

    [Fact]
    public void Parse_ProcessArgumentsOverridePackagedActivationArguments()
    {
        LaunchOptions options = LaunchOptions.Parse(
            ["--theme=light", "--repo=JitHubApp/JitHubV2"],
            "--page=repo --theme=dark --repo=sindresorhus/awesome");

        Assert.Equal("repo", options.Page);
        Assert.Equal("light", options.Theme);
        Assert.Equal("JitHubApp/JitHubV2", options.RepositoryFullName);
    }

    [Fact]
    public void Parse_EnablesWebsiteShowcaseOnlyForTheExplicitCaptureFlag()
    {
        LaunchOptions capture = LaunchOptions.Parse(["--website-showcase"]);
        LaunchOptions normal = LaunchOptions.Parse([]);

        Assert.True(capture.WebsiteShowcase);
        Assert.False(normal.WebsiteShowcase);
    }

    [Fact]
    public void TokenizeActivationArguments_BoundsUntrustedInput()
    {
        Assert.Empty(LaunchOptions.TokenizeActivationArguments(new string('x', 32_768)));
        Assert.Equal(
            ["--scenario=quoted value", "", "--branch=main"],
            LaunchOptions.TokenizeActivationArguments("--scenario=\"quoted value\" \"\" --branch=main"));
    }
}
