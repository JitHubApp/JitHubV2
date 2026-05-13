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
        // Enable diagnostic logger by default in the sample app so any
        // reproduction of the selection-shake bug captures per-frame paint
        // coordinates to text_shaking2.log next to the repo root.
        // Flip to false to silence.
        MarkdownRenderer.Diagnostics.ShakeLogger.Enabled = true;
        _window = new MainWindow();
        _window.Activate();
    }
}
