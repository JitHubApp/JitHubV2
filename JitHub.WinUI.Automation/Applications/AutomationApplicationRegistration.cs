namespace JitHub.WinUI.Automation.Applications;

internal sealed record AutomationApplicationRegistration(
    string AppPath,
    int ProcessId,
    DateTime LaunchStartedUtc,
    IReadOnlySet<int> ExcludedProcessIds);
