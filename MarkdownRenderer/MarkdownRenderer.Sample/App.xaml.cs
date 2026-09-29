using System;
using System.Linq;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Sample;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (args.Arguments.Contains("--markdown-renderer-diagnostics", StringComparison.OrdinalIgnoreCase))
            MarkdownRenderer.Diagnostics.ShakeLogger.Enabled = true;

        _window = Environment.GetCommandLineArgs().Contains("--renderer-lifecycle-probe") ||
            args.Arguments.Contains("--renderer-lifecycle-probe", StringComparison.Ordinal)
            ? new RendererLifecycleProbeWindow()
            : new MainWindow();
        _window.Activate();
    }
}
