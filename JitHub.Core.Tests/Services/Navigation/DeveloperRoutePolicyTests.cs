using JitHub.Core.Services.Navigation;
using System;
using System.IO;
using Xunit;

namespace JitHub.Core.Tests.Services.Navigation;

public sealed class DeveloperRoutePolicyTests
{
    [Fact]
    public void ProductionRoute_IsUnavailableWithoutDeveloperMode()
    {
        Assert.False(DeveloperRoutePolicy.CanOpenDesignLab(
            isDeveloperModeEnabled: false,
            hasIsolatedAutomationRoots: false));
        Assert.False(DeveloperRoutePolicy.CanOpenDevConsole(
            isDeveloperModeEnabled: false));
    }

    [Fact]
    public void DevConsoleRequiresDeveloperModeAndHasNoAutomationBypass()
    {
        Assert.True(DeveloperRoutePolicy.CanOpenDevConsole(
            isDeveloperModeEnabled: true));
        Assert.False(DeveloperRoutePolicy.CanOpenDevConsole(
            isDeveloperModeEnabled: false));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DeveloperAndIsolatedAutomationRoutesRemainAvailable(
        bool developerMode,
        bool automationRoots)
    {
        Assert.True(DeveloperRoutePolicy.CanOpenDesignLab(developerMode, automationRoots));
    }
}
