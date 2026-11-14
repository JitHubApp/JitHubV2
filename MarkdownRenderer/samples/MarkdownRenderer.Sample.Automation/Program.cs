using MarkdownRenderer.Sample.Automation.Applications;
using MarkdownRenderer.Sample.Automation.Options;
using MarkdownRenderer.Sample.Automation.Runs;

namespace MarkdownRenderer.Sample.Automation;

/// <summary>
/// Entry point of the sample UI-automation driver. It resolves the sample
/// executable from the command line and runs either the full probe suite or
/// the <c>--narrator-smoke</c> pass.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        AutomationOptions options = AutomationOptions.Parse(args, SampleAppLauncher.FindDefaultAppPath);
        SampleAppLauncher launcher = SampleAppLauncher.Create(options.AppPath);
        return options.NarratorSmoke
            ? NarratorSmoke.Run(launcher)
            : SampleAutomationSuite.Run(launcher);
    }
}
