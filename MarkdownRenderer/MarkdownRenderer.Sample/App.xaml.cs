using System;
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

        _window = new MainWindow();
        _window.Activate();
    }
}
