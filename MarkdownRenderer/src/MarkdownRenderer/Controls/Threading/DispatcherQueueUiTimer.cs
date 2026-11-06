using System;
using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls.Threading;

/// <summary>An <see cref="IMarkdownUiTimer"/> over a <see cref="DispatcherQueueTimer"/>.</summary>
/// <remarks>
/// The native timer reaches this wrapper and the wrapper reaches its
/// <see cref="Tick"/> handlers, exactly as the native timer would reach them
/// directly; the wrapper adds no other reference.
/// </remarks>
internal sealed class DispatcherQueueUiTimer : IMarkdownUiTimer
{
    private readonly DispatcherQueueTimer _timer;

    internal DispatcherQueueUiTimer(DispatcherQueueTimer timer)
    {
        _timer = timer;
        _timer.Tick += OnTick;
    }

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    public bool IsRepeating
    {
        get => _timer.IsRepeating;
        set => _timer.IsRepeating = value;
    }

    public event EventHandler? Tick;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    private void OnTick(DispatcherQueueTimer sender, object args) => Tick?.Invoke(this, EventArgs.Empty);
}
