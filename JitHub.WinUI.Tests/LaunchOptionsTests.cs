using Xunit;

namespace JitHub.WinUI.Tests;

public sealed class LaunchOptionsTests
{
    [Fact]
    public void Parse_ReadsPackagedLaunchArgumentsAndQuotedCorpusPath()
    {
        LaunchOptions options = LaunchOptions.Parse(
            [],
            "--page=repo --theme=dark --palette=github --repo=sindresorhus/awesome " +
            "--markdown-lifecycle-fixture " +
            "--readme-production-audit " +
            "--markdown-lifecycle-host=MarkdownHost_RepositoryReadme " +
            "--markdown-corpus=\"C:\\readmes\\awesome README.md\"");

        Assert.Equal("repo", options.Page);
        Assert.Equal("dark", options.Theme);
        Assert.Equal("github", options.Palette);
        Assert.Equal("sindresorhus/awesome", options.RepositoryFullName);
        Assert.True(options.MarkdownLifecycleFixture);
        Assert.True(options.ReadmeProductionAudit);
        Assert.Equal("MarkdownHost_RepositoryReadme", options.MarkdownLifecycleHost);
        Assert.Equal("C:\\readmes\\awesome README.md", options.MarkdownCorpusPath);
    }

    [Fact]
    public void Parse_ReadmeProductionAuditRequiresExplicitFlag()
    {
        Assert.True(LaunchOptions.Parse(["--readme-production-audit"]).ReadmeProductionAudit);
        Assert.False(LaunchOptions.Parse([]).ReadmeProductionAudit);
    }

    [Fact]
    public void ReadmeProductionAudit_RequiresStablePositiveAccountPartition()
    {
        const string variable = "JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID";
        string? previous = System.Environment.GetEnvironmentVariable(variable);
        try
        {
            LaunchOptions options = LaunchOptions.Parse(["--readme-production-audit"]);
            System.Environment.SetEnvironmentVariable(variable, "9843127");
            Assert.Equal(9_843_127, options.ResolveReadmeAuditAccountId());

            System.Environment.SetEnvironmentVariable(variable, "current");
            Assert.Throws<System.InvalidOperationException>(() => options.ResolveReadmeAuditAccountId());
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable, previous);
        }
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
