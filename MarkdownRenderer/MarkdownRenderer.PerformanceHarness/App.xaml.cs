using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.PerformanceHarness;

public partial class App : Application
{
    private PerformanceWindow? _window;

    public App()
    {
        UnhandledException += OnUnhandledException;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Debug.WriteLine($"[PerfHarness] launch args: {string.Join(" | ", Program.Arguments)}");
        PerformanceOptions options;
        try
        {
            options = PerformanceOptions.Parse(Program.Arguments);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[PerfHarness] option failure: {exception}");
            Console.Error.WriteLine(exception.Message);
            Program.TryWriteStartupFailureReport(exception);
            Environment.Exit(2);
            return;
        }

        _window = new PerformanceWindow(options);
        Debug.WriteLine("[PerfHarness] window constructed");
        _window.Activate();
        Debug.WriteLine("[PerfHarness] window activated");
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs eventArgs)
    {
        Debug.WriteLine($"[PerfHarness] unhandled exception: {eventArgs.Exception}");
        if (_window?.TryWriteFatalReport(eventArgs.Exception) != true)
            return;

        // A valid invocation must leave a machine-readable, explicitly failed
        // report even if WinUI raises an exception outside the measured task.
        eventArgs.Handled = true;
        Environment.Exit(1);
    }
}
