using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace JitHub.Services.Markdown;

/// <summary>
/// Makes optional Windows runtime event sources best-effort without weakening
/// exception handling in rendering or application logic.
/// </summary>
internal static class RuntimeEventSubscription
{
    private static int _createFailureReported;
    private static int _subscribeFailureReported;
    private static int _unsubscribeFailureReported;

    public static T? TryCreate<T>(Func<T> factory, string sourceName)
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

    public static bool TrySubscribe(Action subscribe, string eventName)
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

    public static void TryUnsubscribe(Action unsubscribe, bool wasSubscribed, string eventName)
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

    private static void ReportOnce(ref int reported, Exception exception, string category)
    {
        if (Interlocked.Exchange(ref reported, 1) == 0)
        {
            JitHub.WinUI.Helpers.HandledFailureReporter.Report(exception, category);
        }
    }
}
