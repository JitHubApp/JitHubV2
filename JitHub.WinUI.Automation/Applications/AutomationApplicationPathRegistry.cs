using FlaUI.Core;

namespace JitHub.WinUI.Automation.Applications;

internal static class AutomationApplicationPathRegistry
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Application, AutomationApplicationRegistration> Paths = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, AutomationApplicationRegistration> OwnedProcesses = new();

    public static void Register(
        Application application,
        string appPath,
        int processId,
        DateTime launchStartedUtc,
        IReadOnlySet<int> excludedProcessIds)
    {
        Paths.Remove(application);
        var registration = new AutomationApplicationRegistration(
            Path.GetFullPath(appPath),
            processId,
            launchStartedUtc,
            new HashSet<int>(excludedProcessIds));
        Paths.Add(application, registration);
        if (processId > 0)
        {
            OwnedProcesses[processId] = registration;
        }
    }

    public static bool TryGet(
        Application application,
        out AutomationApplicationRegistration? registration)
    {
        if (Paths.TryGetValue(application, out AutomationApplicationRegistration? current))
        {
            registration = current;
            return true;
        }

        registration = null;
        return false;
    }

    public static IReadOnlyList<AutomationApplicationRegistration> GetOwnedRegistrations(string appPath)
    {
        string normalizedPath = Path.GetFullPath(appPath);
        return OwnedProcesses.Values
            .Where(registration => string.Equals(
                registration.AppPath,
                normalizedPath,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public static void Forget(Application application)
    {
        if (Paths.TryGetValue(application, out AutomationApplicationRegistration? registration))
        {
            OwnedProcesses.TryRemove(registration.ProcessId, out _);
            Paths.Remove(application);
        }
    }

    public static void Forget(int processId)
    {
        if (processId > 0)
        {
            OwnedProcesses.TryRemove(processId, out _);
        }
    }
}
