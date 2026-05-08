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
        // ShakeLogger is opt-in: flip Enabled=true here to capture per-frame
        // paint coordinates while reproducing the selection vibration bug.
        // MarkdownRenderer.Diagnostics.ShakeLogger.Enabled = true;
        _window = new MainWindow();
        _window.Activate();
    }
}
