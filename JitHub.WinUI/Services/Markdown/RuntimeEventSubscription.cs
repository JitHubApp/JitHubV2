using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace JitHub.Services.Markdown;

/// <summary>
/// Makes optional Windows runtime event sources best-effort without weakening
/// exception handling in rendering or application logic.
/// </summary>
/// <remarks>
/// Each kind of failure is reported once per instance. The app owns one instance
/// (see <c>App.MarkdownRuntimeEvents</c>) and passes it to its Markdown hosts.
/// </remarks>
internal sealed class RuntimeEventSubscription
{
    private readonly Action<Exception, string> _report;
    private int _createFailureReported;
    private int _subscribeFailureReported;
    private int _unsubscribeFailureReported;

    /// <summary>Creates a subscription helper that reports through <c>HandledFailureReporter</c>.</summary>
    internal RuntimeEventSubscription()
        : this(static (exception, category) => JitHub.WinUI.Helpers.HandledFailureReporter.Report(exception, category))
    {
    }

    /// <param name="report">Receives the first failure of each kind and its category.</param>
    internal RuntimeEventSubscription(Action<Exception, string> report)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    public T? TryCreate<T>(Func<T> factory, string sourceName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        try
        {
            return factory();
        }
        catch (COMException exception)
        {
            ReportOnce(ref _createFailureReported, exception, "markdown-runtime-source");
            return null;
        }
    }

    public bool TrySubscribe(Action subscribe, string eventName)
    {
        ArgumentNullException.ThrowIfNull(subscribe);
        try
        {
            subscribe();
            return true;
        }
        catch (COMException exception)
        {
            ReportOnce(ref _subscribeFailureReported, exception, "markdown-runtime-event-subscribe");
            return false;
        }
    }

    public void TryUnsubscribe(Action unsubscribe, bool wasSubscribed, string eventName)
    {
        ArgumentNullException.ThrowIfNull(unsubscribe);
        if (!wasSubscribed)
        {
            return;
        }

        try
        {
            unsubscribe();
        }
        catch (COMException exception)
        {
            ReportOnce(ref _unsubscribeFailureReported, exception, "markdown-runtime-event-unsubscribe");
        }
    }

    private void ReportOnce(ref int reported, Exception exception, string category)
    {
        if (Interlocked.Exchange(ref reported, 1) == 0)
        {
            _report(exception, category);
        }
    }
}
