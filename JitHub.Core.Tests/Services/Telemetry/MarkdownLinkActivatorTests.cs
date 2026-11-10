using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Core.Services.Navigation;
using JitHub.Core.Services.Telemetry;
using JitHub.Markdown.Links;
using JitHub.Markdown.Telemetry;
using MarkdownRenderer.Images;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace JitHub.Core.Tests.Services.Telemetry;

public sealed class MarkdownLinkActivatorTests
{
    private static readonly Uri GitHubBase = new("https://github.com/owner/repo/blob/main/README.md");

    [Fact]
    public async Task SupportedGitHubRoute_OpensInAppWhenTheShellConfirmsIt()
    {
        var harness = new ActivatorHarness();
        harness.Navigator.Result = true;

        await harness.Activator.ActivateAsync("https://github.com/owner/repo/issues/7", GitHubBase, null);

        Assert.Equal([new Uri("https://github.com/owner/repo/issues/7")], harness.Navigator.Opened);
        await harness.Launcher.DidNotReceive().LaunchAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>());
        Assert.Equal(TelemetryTaxonomy.Results.Success, harness.LastResult);
    }

    [Theory]
    [InlineData("https://github.com/octocat")]
    [InlineData("https://github.com/octocat/Hello-World")]
    public async Task UserAndRepositoryRoots_OpenInAppWhenTheShellConfirmsThem(string url)
    {
        var harness = new ActivatorHarness();
        harness.Navigator.Result = true;

        await harness.Activator.ActivateAsync(url, GitHubBase, null);

        Assert.Equal([new Uri(url)], harness.Navigator.Opened);
        await harness.Launcher.DidNotReceive().LaunchAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>());
        Assert.Equal(TelemetryTaxonomy.Results.Success, harness.LastResult);
    }

    [Fact]
    public async Task RelativeLinkWithoutRepositoryContext_NeverAsksTheShellToRouteIt()
    {
        var harness = new ActivatorHarness();
        harness.Navigator.Result = true;
        harness.Launcher.LaunchAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>()).Returns(true);

        await harness.Activator.ActivateAsync("/octocat/Hello-World", new Uri("https://github.com/"), null);

        Assert.Empty(harness.Navigator.Opened);
        await harness.Launcher.Received(1).LaunchAsync(new Uri("https://github.com/octocat/Hello-World"), Arg.Any<CancellationToken>());
        Assert.Equal(TelemetryTaxonomy.Results.Launched, harness.LastResult);
    }

    [Fact]
    public async Task SupportedGitHubRoute_LaunchesExternallyWhenTheShellDoesNotConfirmIt()
    {
        var harness = new ActivatorHarness();
        harness.Launcher.LaunchAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>()).Returns(true);

        await harness.Activator.ActivateAsync("https://github.com/owner/repo/pull/9", GitHubBase, null);

        await harness.Launcher.Received(1).LaunchAsync(new Uri("https://github.com/owner/repo/pull/9"), Arg.Any<CancellationToken>());
        Assert.Equal(TelemetryTaxonomy.Results.Launched, harness.LastResult);
    }

    [Fact]
    public async Task ExternalLink_LaunchesWhenNoInAppRouteOpens()
    {
        var harness = new ActivatorHarness();
        harness.Launcher.LaunchAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>()).Returns(false);

        await harness.Activator.ActivateAsync("https://example.com/docs", GitHubBase, null);

        await harness.Launcher.Received(1).LaunchAsync(new Uri("https://example.com/docs"), Arg.Any<CancellationToken>());
        Assert.Equal(TelemetryTaxonomy.Results.Failed, harness.LastResult);
    }

    [Fact]
    public async Task UnsafeLink_IsRejectedBeforeAnyRouting()
    {
        var harness = new ActivatorHarness();

        await harness.Activator.ActivateAsync("javascript:alert(1)", GitHubBase, null);

        Assert.Empty(harness.Navigator.Opened);
        await harness.Launcher.DidNotReceive().LaunchAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>());
        Assert.Equal(TelemetryTaxonomy.Results.Rejected, harness.LastResult);
    }

    [Fact]
    public async Task LauncherFailure_IsReportedAsALaunchError()
    {
        var harness = new ActivatorHarness();
        harness.Launcher.LaunchAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Launch failed."));

        await harness.Activator.ActivateAsync("https://example.com/docs", GitHubBase, null);

        Assert.Equal("markdown.error", harness.Events[^1].Name);
        Assert.Equal(TelemetryTaxonomy.ErrorKinds.Launch, harness.Events[^1].Properties["error_kind"]);
    }

    [Fact]
    public async Task RelativeLink_ResolvesAgainstTheDocumentContext()
    {
        var harness = new ActivatorHarness();
        harness.Launcher.LaunchAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>()).Returns(true);
        var source = new MarkdownDocumentSource("owner/repo/docs/guide.md", "owner", "repo", "main", "docs/guide.md");

        await harness.Activator.ActivateAsync(
            "https://example.com/absolute",
            new Uri("https://github.com/owner/repo/blob/main/docs/guide.md"),
            source);

        await harness.Launcher.Received(1).LaunchAsync(new Uri("https://example.com/absolute"), Arg.Any<CancellationToken>());
    }

    private sealed class ActivatorHarness
    {
        public ActivatorHarness()
        {
            ITelemetryService telemetry = Substitute.For<ITelemetryService>();
            telemetry
                .When(static service => service.TrackEvent(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>?>()))
                .Do(call => Events.Add((
                    call.ArgAt<string>(0),
                    new Dictionary<string, string?>(call.ArgAt<IReadOnlyDictionary<string, string?>>(1)!))));
            Activator = new MarkdownLinkActivator(
                Navigator,
                Launcher,
                new MarkdownViewerTelemetry(telemetry, static () => "RepositoryReadme"));
        }

        public RecordingNavigator Navigator { get; } = new();

        public IExternalUriLauncher Launcher { get; } = Substitute.For<IExternalUriLauncher>();

        public List<(string Name, Dictionary<string, string?> Properties)> Events { get; } = [];

        public MarkdownLinkActivator Activator { get; }

        public string? LastResult => Events[^1].Properties["result"];
    }

    private sealed class RecordingNavigator : IMarkdownRouteNavigator
    {
        public bool Result { get; set; }

        public List<Uri> Opened { get; } = [];

        public bool TryOpen(Uri uri)
        {
            Opened.Add(uri);
            return Result;
        }
    }
}
