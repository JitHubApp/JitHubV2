using JitHub.WinUI.Automation.Applications;
using JitHub.WinUI.Automation.Logging;
using JitHub.WinUI.Automation.Native;
using JitHub.WinUI.Automation.Options;
using JitHub.WinUI.Automation.Probes;

NativeMethods.EnablePerMonitorV2DpiAwareness();
var options = CaptureOptions.Parse(args);
Directory.CreateDirectory(options.OutputDirectory);
AutomationDataRoot.PrepareAutomationDataRoot();
AutomationLifecycleLog.Configure(Path.Combine(options.OutputDirectory, "automation-lifecycle.log"));
AutomationLifecycleLog.Write(
    "automation-start",
    $"probe={options.Probe ?? "capture"}; app={options.AppPath}; dataRoot={AutomationDataRoot.GetAutomationDataRoot()}");

int automationExitCode = 0;
try
{
    ProbeCatalog.Run(options);
}
catch (Exception exception)
{
    automationExitCode = 1;
    Console.Error.WriteLine(exception);
}
finally
{
    Environment.ExitCode = automationExitCode;
    AutomationLifecycleLog.Write(
        "probe-completed",
        $"probe={options.Probe ?? "capture"}; automationExitCode={automationExitCode}; status={(automationExitCode == 0 ? "passed" : "failed")}");
}
