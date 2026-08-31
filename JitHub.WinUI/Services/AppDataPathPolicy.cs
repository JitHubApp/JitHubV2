using System;
using System.IO;

namespace JitHub.Services;

internal static class AppDataPathPolicy
{
    internal const string AutomationDataRootEnvironmentVariable = "JITHUB_AUTOMATION_DATA_ROOT";
    private const string PreviewPageEnvironmentVariable = "JITHUB_PREVIEW_PAGE";
    private const string PreviewScenarioEnvironmentVariable = "JITHUB_PREVIEW_SCENARIO";

    internal static bool TryGetAutomationRoots(out string localFolderPath, out string localCachePath) =>
        TryResolveAutomationRoots(
            Environment.GetEnvironmentVariable(AutomationDataRootEnvironmentVariable),
            Environment.GetEnvironmentVariable(PreviewPageEnvironmentVariable),
            Environment.GetEnvironmentVariable(PreviewScenarioEnvironmentVariable),
            out localFolderPath,
            out localCachePath);

    internal static bool TryResolveAutomationRoots(
        string? overrideRoot,
        string? previewPage,
        out string localFolderPath,
        out string localCachePath) =>
        TryResolveAutomationRoots(
            overrideRoot,
            previewPage,
            authScenario: null,
            out localFolderPath,
            out localCachePath);

    internal static bool TryResolveAutomationRoots(
        string? overrideRoot,
        string? previewPage,
        string? authScenario,
        out string localFolderPath,
        out string localCachePath)
    {
        localFolderPath = string.Empty;
        localCachePath = string.Empty;
        bool isExplicitPreview = !string.IsNullOrWhiteSpace(previewPage);
        bool isAuthLifecycleScenario = AuthLifecycleAutomationContext.IsKnownScenario(authScenario);
        if (string.IsNullOrWhiteSpace(overrideRoot) || (!isExplicitPreview && !isAuthLifecycleScenario))
        {
            return false;
        }

        string root = Path.GetFullPath(overrideRoot.Trim());
        localFolderPath = Path.Combine(root, "Local");
        localCachePath = Path.Combine(root, "LocalCache");
        Directory.CreateDirectory(localFolderPath);
        Directory.CreateDirectory(localCachePath);
        return true;
    }
}

internal static class DialogMatrixAutomationScenario
{
    internal const string Name = "compact-dialog-matrix";

    internal static bool IsEnabled
    {
        get
        {
#if DEBUG
            return string.Equals(
                    JitHub.WinUI.Program.CurrentLaunchOptions.Scenario,
                    Name,
                    StringComparison.OrdinalIgnoreCase) &&
                AppDataPathPolicy.TryGetAutomationRoots(out _, out _);
#else
            return false;
#endif
        }
    }
}
