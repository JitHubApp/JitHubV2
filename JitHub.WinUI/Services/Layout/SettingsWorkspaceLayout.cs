namespace JitHub.Services.Layout;

public enum SettingsWorkspaceMode
{
    Wide,
    Compact,
    Narrow
}

public readonly record struct SettingsWorkspaceLayoutState(
    SettingsWorkspaceMode Mode,
    bool IsSectionRailVisible,
    bool IsCompactSelectorVisible,
    bool ShouldStackActions);

public static class SettingsWorkspaceLayout
{
    public const double CompactThresholdWidth = WorkspaceChromeLayout.CompactBreakpoint;
    public const double NarrowThresholdWidth = WorkspaceChromeLayout.NarrowBreakpoint;

    public static SettingsWorkspaceLayoutState Calculate(double availableWidth)
    {
        SettingsWorkspaceMode mode = WorkspaceChromeLayout.Calculate(availableWidth).Mode switch
        {
            WorkspaceChromeMode.Narrow => SettingsWorkspaceMode.Narrow,
            WorkspaceChromeMode.Compact => SettingsWorkspaceMode.Compact,
            _ => SettingsWorkspaceMode.Wide
        };

        return new SettingsWorkspaceLayoutState(
            mode,
            IsSectionRailVisible: mode == SettingsWorkspaceMode.Wide,
            IsCompactSelectorVisible: mode != SettingsWorkspaceMode.Wide,
            ShouldStackActions: mode == SettingsWorkspaceMode.Narrow);
    }
}
