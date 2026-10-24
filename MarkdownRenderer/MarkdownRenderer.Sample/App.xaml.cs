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

        bool selectionDisableProbe = Environment.GetCommandLineArgs().Contains("--renderer-selection-disable-probe") ||
            args.Arguments.Contains("--renderer-selection-disable-probe", StringComparison.Ordinal);
        bool lifecycleProbe = Environment.GetCommandLineArgs().Contains("--renderer-lifecycle-probe") ||
            args.Arguments.Contains("--renderer-lifecycle-probe", StringComparison.Ordinal);
        bool deferredTouchProbe = Environment.GetCommandLineArgs().Contains("--renderer-deferred-touch-probe") ||
            args.Arguments.Contains("--renderer-deferred-touch-probe", StringComparison.Ordinal);
        bool touchLifecycleProbe = Environment.GetCommandLineArgs().Contains("--renderer-touch-lifecycle-probe") ||
            args.Arguments.Contains("--renderer-touch-lifecycle-probe", StringComparison.Ordinal);
        _window = touchLifecycleProbe
            ? new TouchLifecycleProbeWindow()
            : deferredTouchProbe
            ? new DeferredTouchProbeWindow()
            : selectionDisableProbe
            ? new RendererLifecycleProbeWindow(selectionDisableProbe: true)
            : lifecycleProbe
                ? new RendererLifecycleProbeWindow()
                : new MainWindow();
        _window.Activate();
    }
}
