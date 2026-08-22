using Xunit;

namespace JitHub.WinUI.Tests;

public sealed class LaunchOptionsTests
{
    [Fact]
    public void Parse_ReadsPackagedLaunchArgumentsAndQuotedCorpusPath()
    {
        LaunchOptions options = LaunchOptions.Parse(
            [],
            "--page=repo --theme=dark --repo=sindresorhus/awesome " +
            "--markdown-lifecycle-fixture " +
            "--markdown-lifecycle-host=MarkdownHost_RepositoryReadme " +
            "--markdown-corpus=\"C:\\readmes\\awesome README.md\"");

        Assert.Equal("repo", options.Page);
        Assert.Equal("dark", options.Theme);
        Assert.Equal("sindresorhus/awesome", options.RepositoryFullName);
        Assert.True(options.MarkdownLifecycleFixture);
        Assert.Equal("MarkdownHost_RepositoryReadme", options.MarkdownLifecycleHost);
        Assert.Equal("C:\\readmes\\awesome README.md", options.MarkdownCorpusPath);
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
    public void TokenizeActivationArguments_BoundsUntrustedInput()
    {
        Assert.Empty(LaunchOptions.TokenizeActivationArguments(new string('x', 32_768)));
        Assert.Equal(
            ["--scenario=quoted value", "", "--branch=main"],
            LaunchOptions.TokenizeActivationArguments("--scenario=\"quoted value\" \"\" --branch=main"));
    }
}
