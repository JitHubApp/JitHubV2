namespace JitHub.WinUI.Automation.Applications;

/// <summary>
/// Locates and prepares the isolated data root the app under test runs against.
/// </summary>
internal static class AutomationDataRoot
{
    internal static string GetAutomationDataRoot() => Path.Combine(
        Path.GetTempPath(),
        "JitHub.WinUI.Automation",
        $"run-{Environment.ProcessId}");

    internal static void PrepareAutomationDataRoot()
    {
        string root = GetAutomationDataRoot();
        bool preserveDataRoot = string.Equals(
            Environment.GetEnvironmentVariable("JITHUB_AUTOMATION_PRESERVE_DATA_ROOT"),
            "1",
            StringComparison.Ordinal);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        if (preserveDataRoot)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        };
    }
}
