using System;
using JitHub.Core.Services.Storage;

namespace JitHub.Services;

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
