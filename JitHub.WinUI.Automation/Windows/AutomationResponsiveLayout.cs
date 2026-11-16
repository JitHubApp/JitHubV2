namespace JitHub.WinUI.Automation.Windows;

internal static class AutomationResponsiveLayout
{
    // Mirrors ShellResponsiveLayout.RailCollapseWidth. Keeping the real-app
    // probes on the production collapse order avoids false wide-layout
    // assertions when the Windows work area clamps a requested viewport.
    public const int ShellRailCollapseWidth = 1298;
}
