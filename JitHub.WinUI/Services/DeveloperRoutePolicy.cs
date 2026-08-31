namespace JitHub.Services;

public static class DeveloperRoutePolicy
{
    public static bool CanOpenDesignLab(bool isDeveloperModeEnabled, bool hasIsolatedAutomationRoots) =>
        isDeveloperModeEnabled || hasIsolatedAutomationRoots;

    public static bool CanOpenDevConsole(bool isDeveloperModeEnabled) =>
        isDeveloperModeEnabled;
}
