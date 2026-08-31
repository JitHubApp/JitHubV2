using System.Runtime.InteropServices;
using JitHub.WinUI.Helpers;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class LocalizedResourceTextTests
{
    [Fact]
    public void GetString_UsesFallback_InNormalNonAppTestHost()
    {
        Assert.Equal(
            "Normal test fallback",
            LocalizedResourceText.GetString(
                "Automation.Missing.Normal.Test.Resource",
                "Normal test fallback"));
    }

    [Fact]
    public void GetString_UsesFallback_WhenPriResourceMapIsUnavailableOrKeyIsMissing()
    {
        using IDisposable restore = LocalizedResourceText.OverrideResourceLookupFactoryForTests(
            static () => null);

        string value = LocalizedResourceText.GetString(
            "Automation.Missing.Resource.Key",
            "Fallback text");

        Assert.Equal("Fallback text", value);
        Assert.Equal(
            "Compatibility fallback",
            LocalizedResourceText.Get("Automation.Missing.Compatibility.Key", "Compatibility fallback"));
    }

    [Fact]
    public void Format_UsesFallbackFormat_WhenPriResourceMapIsUnavailableOrKeyIsMissing()
    {
        using IDisposable restore = LocalizedResourceText.OverrideResourceLookupFactoryForTests(
            static () => throw new COMException("The PRI resource map is unavailable."));

        string value = LocalizedResourceText.Format(
            "Automation.Missing.Format.Key",
            "Loaded {0} items",
            3);

        Assert.Equal("Loaded 3 items", value);
    }

    [Fact]
    public void GetString_UsesResolvedRuntimeResource()
    {
        using IDisposable restore = LocalizedResourceText.OverrideResourceLookupFactoryForTests(
            static () => static key => key == "Shell/Navigation/CollapsePane"
                ? "⟦Collapse navigation pane ~~~~~~~~~⟧"
                : null);

        Assert.Equal(
            "⟦Collapse navigation pane ~~~~~~~~~⟧",
            LocalizedResourceText.GetString(
                "Shell.Navigation.CollapsePane",
                "Collapse navigation pane"));
    }

    [Fact]
    public void Format_UsesFallback_WhenLocalizedFormatIsMalformed()
    {
        using IDisposable restore = LocalizedResourceText.OverrideResourceLookupFactoryForTests(
            static () => static _ => "Loaded {0 items");

        Assert.Equal(
            "Loaded 3 items",
            LocalizedResourceText.Format(
                "Automation.Malformed.Format",
                "Loaded {0} items",
                3));
    }

    [Fact]
    public async Task ResourceLookupOverride_IsIsolatedFromParallelExecutionContexts()
    {
        var overrideInstalled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowScopedLookup = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string> scopedLookup = Task.Run(async () =>
        {
            using IDisposable restore = LocalizedResourceText.OverrideResourceLookupFactoryForTests(
                static () => static _ => "Scoped value");
            overrideInstalled.SetResult(true);
            await allowScopedLookup.Task;
            return LocalizedResourceText.GetString("Automation.Scoped.Key", "Scoped fallback");
        });

        await overrideInstalled.Task;
        Assert.Equal(
            "Parallel fallback",
            LocalizedResourceText.GetString("Automation.Parallel.Key", "Parallel fallback"));

        allowScopedLookup.SetResult(true);
        Assert.Equal("Scoped value", await scopedLookup);
    }

}
