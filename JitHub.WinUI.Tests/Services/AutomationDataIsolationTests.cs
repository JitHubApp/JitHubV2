using System.IO;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class AutomationDataIsolationTests
{
    [Fact]
    public void AutomationRootRequiresAnExplicitPreviewLaunch()
    {
        string root = Path.Combine(Path.GetTempPath(), "JitHub-AutomationDataIsolationTests");

        Assert.False(AppDataPathPolicy.TryResolveAutomationRoots(root, null, out _, out _));
        Assert.False(AppDataPathPolicy.TryResolveAutomationRoots(null, "stars", out _, out _));
        Assert.False(AppDataPathPolicy.TryResolveAutomationRoots(root, null, "unknown-scenario", out _, out _));
    }

    [Fact]
    public void AutomationRootSeparatesLocalAndCacheData()
    {
        string root = Path.Combine(Path.GetTempPath(), "JitHub-AutomationDataIsolationTests", "run-42");

        Assert.True(AppDataPathPolicy.TryResolveAutomationRoots(root, "stars", out string local, out string cache));
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "Local"), local);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "LocalCache"), cache);
        Assert.NotEqual(local, cache);
    }

    [Fact]
    public void AuthLifecycleScenarioEnablesAnIsolatedRootWithoutAPageOverride()
    {
        string root = Path.Combine(Path.GetTempPath(), "JitHub-AutomationDataIsolationTests", "auth-run");

        Assert.True(AppDataPathPolicy.TryResolveAutomationRoots(
            root,
            previewPage: null,
            AuthLifecycleScenario.ExpiredToken,
            out string local,
            out string cache));
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "Local"), local);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "LocalCache"), cache);
    }
}
