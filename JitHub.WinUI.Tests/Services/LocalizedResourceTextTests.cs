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
        using IDisposable restore = LocalizedResourceText.OverrideResourceLoaderFactoryForTests(
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
        using IDisposable restore = LocalizedResourceText.OverrideResourceLoaderFactoryForTests(
            static () => throw new COMException("The PRI resource map is unavailable."));

        string value = LocalizedResourceText.Format(
            "Automation.Missing.Format.Key",
            "Loaded {0} items",
            3);

        Assert.Equal("Loaded 3 items", value);
    }

}
