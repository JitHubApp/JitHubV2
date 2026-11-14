using MarkdownRenderer.Sample.Automation;
using MarkdownRenderer.Sample.Automation.Options;
using MarkdownRenderer.Sample.Automation.Runs;
using Xunit;

namespace MarkdownRenderer.Sample.Automation.Tests.Options;

public sealed class AutomationOptionsTests
{
    [Fact]
    public void ExplicitAppPathWinsAndSkipsTheDefaultLookup()
    {
        bool lookedUp = false;
        AutomationOptions options = AutomationOptions.Parse(
            ["--app-path", @"C:\app\MarkdownRenderer.Sample.exe"],
            () =>
            {
                lookedUp = true;
                return @"C:\default\MarkdownRenderer.Sample.exe";
            });

        Assert.Equal(@"C:\app\MarkdownRenderer.Sample.exe", options.AppPath);
        Assert.False(options.NarratorSmoke);
        Assert.False(lookedUp);
    }

    [Fact]
    public void MissingAppPathFallsBackToTheDefaultLookup()
    {
        AutomationOptions options = AutomationOptions.Parse([], () => @"C:\default\MarkdownRenderer.Sample.exe");

        Assert.Equal(@"C:\default\MarkdownRenderer.Sample.exe", options.AppPath);
    }

    [Fact]
    public void TrailingAppPathSwitchWithoutAValueIsIgnored()
    {
        AutomationOptions options = AutomationOptions.Parse(["--app-path"], () => @"C:\default\MarkdownRenderer.Sample.exe");

        Assert.Equal(@"C:\default\MarkdownRenderer.Sample.exe", options.AppPath);
    }

    [Fact]
    public void AppPathSwitchIsCaseSensitive()
    {
        AutomationOptions options = AutomationOptions.Parse(
            ["--APP-PATH", @"C:\app\MarkdownRenderer.Sample.exe"],
            () => @"C:\default\MarkdownRenderer.Sample.exe");

        Assert.Equal(@"C:\default\MarkdownRenderer.Sample.exe", options.AppPath);
    }

    [Theory]
    [InlineData("--narrator-smoke")]
    [InlineData("--Narrator-Smoke")]
    public void NarratorSmokeSwitchIsCaseInsensitive(string narratorSwitch)
    {
        AutomationOptions options = AutomationOptions.Parse(
            [narratorSwitch, "--app-path", "sample.exe"],
            () => null);

        Assert.True(options.NarratorSmoke);
        Assert.Equal("sample.exe", options.AppPath);
    }

    [Fact]
    public void NoAppPathAnywhereFailsWithTheSwitchToPass()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => AutomationOptions.Parse(["--narrator-smoke"], () => null));

        Assert.Equal("Cannot locate MarkdownRenderer.Sample.exe. Pass --app-path <exe>.", error.Message);
    }
}
