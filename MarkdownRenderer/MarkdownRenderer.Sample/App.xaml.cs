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
        // Enable async shake diagnostic logging — drains via Debug.WriteLine
        // on a background task so it doesn't perturb selection-drag perf.
        MarkdownRenderer.Diagnostics.ShakeLogger.Enabled = true;
        _window = new MainWindow();
        _window.Activate();
    }
}
