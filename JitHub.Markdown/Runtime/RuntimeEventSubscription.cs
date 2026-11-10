using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace JitHub.Markdown.Runtime;

/// <summary>
/// Makes optional Windows runtime event sources best-effort without weakening
/// exception handling in rendering or application logic.
/// </summary>
/// <remarks>
/// Each kind of failure is reported once per instance. The app owns one instance
/// (see <c>App.MarkdownRuntimeEvents</c>), reports through its handled-failure
/// sink, and passes the instance to its Markdown hosts.
/// </remarks>
public sealed class RuntimeEventSubscription
{
    private readonly Action<Exception, string> _report;
    private int _createFailureReported;
    private int _subscribeFailureReported;
    private int _unsubscribeFailureReported;

    /// <param name="report">Receives the first failure of each kind and its category.</param>
    public RuntimeEventSubscription(Action<Exception, string> report)
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
