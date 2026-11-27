using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MarkdownRenderer.ScrollFidelityProbe.Hosting;

/// <summary>The maximized probe window. It starts the run once its host grid loads.</summary>
internal sealed class ProbeWindow : Window
{
    private readonly Grid _host = new();
    private readonly ProbeRun _run;
    private bool _started;

    internal ProbeWindow(ProbeOptions options)
    {
        Title = "Markdown scroll fidelity probe";
        Content = _host;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
        _run = new ProbeRun(this, _host, options);
        _host.Loaded += OnHostLoaded;
    }

    internal void WriteFailure(Exception exception) => _run.WriteFailure(exception);

    private void OnHostLoaded(object sender, RoutedEventArgs e)
    {
        if (_started)
            return;
        _started = true;
        _ = _run.RunAsync();
    }
}
