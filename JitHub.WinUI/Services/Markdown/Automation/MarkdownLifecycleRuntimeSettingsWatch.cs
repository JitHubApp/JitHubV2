using System;
using Microsoft.UI.Dispatching;

namespace JitHub.Services.Markdown.Automation;

/// <summary>
/// Polls the lifecycle harness's runtime settings file and reports each new
/// revision, so harness-driven text scale changes reach a loaded host.
/// <c>partial</c> only so CsWinRT can generate its ABI projection of IDisposable.
/// </summary>
internal sealed partial class MarkdownLifecycleRuntimeSettingsWatch : IDisposable
{
    private readonly MarkdownLifecycleAutomationBridge _bridge;
    private readonly Action _onChanged;
    private DispatcherQueueTimer? _timer;
    private int _revision;

    /// <param name="dispatcherQueue">The host's dispatcher queue, which runs the poll.</param>
    /// <param name="bridge">The app's lifecycle automation bridge, which reads the settings file.</param>
    /// <param name="onChanged">Called on the dispatcher when the revision changes.</param>
    public MarkdownLifecycleRuntimeSettingsWatch(
        DispatcherQueue dispatcherQueue,
        MarkdownLifecycleAutomationBridge bridge,
        Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _revision = _bridge.GetRuntimeSettingsRevision();
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.IsRepeating = true;
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    /// <summary>Stops polling.</summary>
    public void Dispose()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
            _timer = null;
        }
    }

    private void Timer_Tick(DispatcherQueueTimer sender, object args)
    {
        int revision = _bridge.GetRuntimeSettingsRevision();
        if (revision > 0 && revision != _revision)
        {
            _revision = revision;
            _onChanged();
        }
    }
}
