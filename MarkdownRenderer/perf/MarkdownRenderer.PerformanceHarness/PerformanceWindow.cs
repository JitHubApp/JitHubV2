using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// The benchmark window: a single host grid for the renderer under
/// measurement. It starts its <see cref="PerformanceRun"/> once the host is
/// loaded; the run owns the measurements, the report and the exit code.
/// </summary>
internal sealed class PerformanceWindow : Window
{
    private readonly Grid _host = new();
    private readonly PerformanceRun _run;
    private bool _started;

    internal PerformanceWindow(PerformanceOptions options)
    {
        Title = "MarkdownRenderer pinned-machine performance harness";
        AppWindow.Resize(new SizeInt32(1100, 760));
        Content = _host;
        _host.Loaded += OnHostLoaded;
        // The run stamps the report's start time and hashes the build after
        // the window is sized, as the harness always has.
        _run = new PerformanceRun(this, _host, options);
    }

    internal bool TryWriteFatalReport(Exception exception)
        => _run.TryWriteFatalReport(exception);

    private void OnHostLoaded(object sender, RoutedEventArgs e)
    {
        if (_started)
            return;
        _started = true;
        _ = _run.RunAsync();
    }
}
