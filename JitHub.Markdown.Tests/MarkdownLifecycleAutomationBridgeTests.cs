using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;

namespace JitHub.Markdown.Tests;

public sealed class MarkdownLifecycleAutomationBridgeTests : IDisposable
{
    private const int ProcessId = 4242;
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "JitHub.MarkdownLifecycleBridgeTests",
        Guid.NewGuid().ToString("N"));

    public MarkdownLifecycleAutomationBridgeTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory does not affect other tests.
        }
    }

    [Fact]
    public void ProductionProcessIsInertWithoutTheFixture()
    {
        _environment["JITHUB_AUTOMATION_HIGH_CONTRAST"] = "1";
        _environment["JITHUB_AUTOMATION_RESOURCE_MAP_ABSENT"] = "1";
        _environment["JITHUB_MARKDOWN_HOST_READY_PATH"] = PathFor("ready.json");
        MarkdownLifecycleAutomationBridge bridge = Create(launchFixtureEnabled: false);

        bridge.SignalHostReady("MarkdownHost_ProfileReadme");

        Assert.False(bridge.IsEnabled);
        Assert.False(bridge.IsHighContrastEnabled);
        Assert.False(bridge.IsResourceMapForcedAbsent);
        Assert.Null(bridge.TargetHost);
        Assert.False(bridge.TargetsHost("MarkdownHost_ProfileReadme"));
        Assert.Null(bridge.GetTextScaleFactor());
        Assert.False(File.Exists(PathFor("ready.json")));
    }

    [Fact]
    public void FixtureCanBeEnabledByLaunchArgumentsOrEnvironment()
    {
        Assert.True(Create(launchFixtureEnabled: true).IsEnabled);

        _environment["JITHUB_MARKDOWN_LIFECYCLE_FIXTURE"] = "1";
        _environment["JITHUB_AUTOMATION_RESOURCE_MAP_ABSENT"] = "1";
        MarkdownLifecycleAutomationBridge bridge = Create(launchFixtureEnabled: false);

        Assert.True(bridge.IsEnabled);
        Assert.True(bridge.IsResourceMapForcedAbsent);
    }

    [Fact]
    public void LaunchTargetHostOverridesTheEnvironmentAndScopesHosts()
    {
        _environment["JITHUB_MARKDOWN_LIFECYCLE_HOST"] = "MarkdownHost_Comment";
        MarkdownLifecycleAutomationBridge bridge = Create(launchFixtureEnabled: true, launchTargetHost: " MarkdownHost_ProfileReadme ");

        Assert.Equal("MarkdownHost_ProfileReadme", bridge.TargetHost);
        Assert.True(bridge.TargetsHost("MarkdownHost_ProfileReadme_Main"));
        Assert.False(bridge.TargetsHost("MarkdownHost_Comment_1"));
        Assert.False(bridge.TargetsHost(" "));
        Assert.True(Create(launchFixtureEnabled: true).TargetsHost("MarkdownHost_Comment_1"));
    }

    [Fact]
    public void HostReadyIsSignaledOncePerHostInstance()
    {
        string path = PathFor("host-ready.json");
        _environment["JITHUB_MARKDOWN_HOST_READY_PATH"] = path;
        MarkdownLifecycleAutomationBridge bridge = Create(launchFixtureEnabled: true);

        bridge.SignalHostReady("MarkdownHost_A");
        File.Delete(path);
        bridge.SignalHostReady("MarkdownHost_A");
        Assert.False(File.Exists(path));

        bridge.SignalHostReady("MarkdownHost_B");
        using JsonDocument signal = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(ProcessId, signal.RootElement.GetProperty("ProcessId").GetInt32());
        Assert.Equal("MarkdownHost_B", signal.RootElement.GetProperty("Stage").GetString());

        // A new bridge owns its own once-only state.
        File.Delete(path);
        Create(launchFixtureEnabled: true).SignalHostReady("MarkdownHost_B");
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RuntimeSettingsFileDrivesTextScaleAndRevision()
    {
        string path = PathFor("runtime-settings.json");
        _environment["JITHUB_MARKDOWN_RUNTIME_SETTINGS_PATH"] = path;
        _environment["JITHUB_AUTOMATION_TEXT_SCALE_FACTOR"] = "1.5";
        MarkdownLifecycleAutomationBridge bridge = Create(launchFixtureEnabled: true);

        Assert.Equal(1.5, bridge.GetTextScaleFactor());
        Assert.Equal(0, bridge.GetRuntimeSettingsRevision());

        File.WriteAllText(path, """{"TextScaleFactor":7,"Revision":3}""");
        Assert.Equal(3, bridge.GetTextScaleFactor());
        Assert.Equal(3, bridge.GetRuntimeSettingsRevision());

        File.WriteAllText(path, """{"TextScaleFactor":""");
        Assert.Equal(1.5, bridge.GetTextScaleFactor());
        Assert.Equal(0, bridge.GetRuntimeSettingsRevision());
    }

    [Fact]
    public void LinkRoutesAppendOneJsonLineForTargetedHostsOnly()
    {
        string path = PathFor(Path.Combine("evidence", "links.jsonl"));
        _environment["JITHUB_MARKDOWN_LINK_EVIDENCE_PATH"] = path;
        MarkdownLifecycleAutomationBridge bridge = Create(launchFixtureEnabled: true, launchTargetHost: "MarkdownHost_Comment");

        Assert.False(bridge.RecordLinkRoute("MarkdownHost_ProfileReadme", new Uri("https://github.com/a"), "external"));
        Assert.False(bridge.RecordLinkRoute("MarkdownHost_Comment_1", new Uri("https://github.com/a"), " "));
        Assert.True(bridge.RecordLinkRoute("MarkdownHost_Comment_1", new Uri("https://github.com/a"), "external"));
        Assert.True(bridge.RecordLinkRoute("MarkdownHost_Comment_1", new Uri("https://github.com/b"), "in-app"));

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        using JsonDocument second = JsonDocument.Parse(lines[1]);
        Assert.Equal("MarkdownHost_Comment_1", second.RootElement.GetProperty("Host").GetString());
        Assert.Equal("in-app", second.RootElement.GetProperty("Disposition").GetString());
        Assert.Equal("https://github.com/b", second.RootElement.GetProperty("Uri").GetString());
    }

    [Fact]
    public void ResourceMapFallbackIsSignaledOnlyWhenForcedAbsent()
    {
        string path = PathFor("resource-map.json");
        _environment["JITHUB_AUTOMATION_RESOURCE_MAP_EVIDENCE_PATH"] = path;
        MarkdownLifecycleAutomationBridge bridge = Create(launchFixtureEnabled: true);

        bridge.SignalResourceMapFallback("fallback");
        Assert.False(File.Exists(path));

        _environment["JITHUB_AUTOMATION_RESOURCE_MAP_ABSENT"] = "1";
        bridge.SignalResourceMapFallback("fallback");
        using JsonDocument signal = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("fallback", signal.RootElement.GetProperty("Fallback").GetString());
    }

    private MarkdownLifecycleAutomationBridge Create(bool launchFixtureEnabled, string? launchTargetHost = null) =>
        new(
            launchFixtureEnabled,
            launchTargetHost,
            name => _environment.TryGetValue(name, out string? value) ? value : null,
            ProcessId);

    private string PathFor(string name) => Path.Combine(_directory, name);
}
