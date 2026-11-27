using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MarkdownRenderer.ScrollFidelityProbe.Scenarios;

/// <summary>
/// Records, on every UI composition tick, the interval since the previous
/// tick and the scroll offset the UI thread sees, plus the instants that
/// scenario jumps landed. UI ticks that stretch out show the renderer
/// competing with input for the UI thread.
/// </summary>
internal sealed class ScrollRecorder : IDisposable
{
    private readonly ScrollViewer _scrollViewer;
    private long _previous;
    private bool _attached;

    internal ScrollRecorder(ScrollViewer scrollViewer)
    {
        _scrollViewer = scrollViewer;
    }

    internal List<double> UiTickIntervalsMs { get; } = [];

    internal List<(long Timestamp, double Offset)> Offsets { get; } = [];

    internal List<long> JumpLandings { get; } = [];

    internal void Start()
    {
        _previous = 0;
        CompositionTarget.Rendering += OnRendering;
        _attached = true;
    }

    internal void MarkJumpLanded() => JumpLandings.Add(Stopwatch.GetTimestamp());

    public void Dispose()
    {
        if (_attached)
            CompositionTarget.Rendering -= OnRendering;
        _attached = false;
    }

    private void OnRendering(object? sender, object args)
    {
        long now = Stopwatch.GetTimestamp();
        if (_previous != 0)
            UiTickIntervalsMs.Add(Stopwatch.GetElapsedTime(_previous, now).TotalMilliseconds);
        _previous = now;
        Offsets.Add((now, _scrollViewer.VerticalOffset));
    }
}
