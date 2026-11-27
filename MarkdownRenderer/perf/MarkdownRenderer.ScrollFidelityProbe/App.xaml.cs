using System.Diagnostics;
using MarkdownRenderer.ScrollFidelityProbe.Hosting;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.ScrollFidelityProbe;

public partial class App : Application
{
    private readonly string[] _arguments;
    private ProbeWindow? _window;
    private ExceptionLog? _exceptions;

    public App(string[] arguments)
    {
        _arguments = arguments;
        UnhandledException += OnUnhandledException;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ProbeOptions options = ProbeOptions.Parse(_arguments);
        _exceptions = ExceptionLog.StartFor(options.OutputPath);
        _window = new ProbeWindow(options);
        _window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs eventArgs)
    {
        Debug.WriteLine($"[ScrollProbe] unhandled exception: {eventArgs.Exception}");
        _window?.WriteFailure(eventArgs.Exception);
        eventArgs.Handled = true;
        Environment.Exit(1);
    }
}
