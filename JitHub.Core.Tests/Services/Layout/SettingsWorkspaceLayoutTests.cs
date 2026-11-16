using JitHub.Core.Services.Layout;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.Core.Tests.Services.Layout;

public sealed class SettingsWorkspaceLayoutTests
{
    [Theory]
    [InlineData(1080, SettingsWorkspaceMode.Wide, true, false, false)]
    [InlineData(820, SettingsWorkspaceMode.Wide, true, false, false)]
    [InlineData(819, SettingsWorkspaceMode.Compact, false, true, false)]
    [InlineData(620, SettingsWorkspaceMode.Compact, false, true, false)]
    [InlineData(619, SettingsWorkspaceMode.Narrow, false, true, true)]
    [InlineData(320, SettingsWorkspaceMode.Narrow, false, true, true)]
    public void Calculate_OwnsStableSectionNavigationAndActionReflow(
        double width,
        SettingsWorkspaceMode expectedMode,
        bool expectedRail,
        bool expectedSelector,
        bool expectedStackedActions)
    {
        SettingsWorkspaceLayoutState state = SettingsWorkspaceLayout.Calculate(width);

        Assert.Equal(expectedMode, state.Mode);
        Assert.Equal(expectedRail, state.IsSectionRailVisible);
        Assert.Equal(expectedSelector, state.IsCompactSelectorVisible);
        Assert.Equal(expectedStackedActions, state.ShouldStackActions);
    }
}
