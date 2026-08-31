using System;
using System.Threading;

namespace JitHub.WinUI.Helpers;

internal static class HandledFailureReporter
{
    private static Action<Exception, string>? _exceptionSink;
    private static Action<string, string>? _messageSink;

    public static void Configure(
        Action<Exception, string> exceptionSink,
        Action<string, string> messageSink)
    {
        ArgumentNullException.ThrowIfNull(exceptionSink);
        ArgumentNullException.ThrowIfNull(messageSink);
        Volatile.Write(ref _exceptionSink, exceptionSink);
        Volatile.Write(ref _messageSink, messageSink);
    }

    public static void Report(Exception exception, string category)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            Volatile.Read(ref _exceptionSink)?.Invoke(exception, category);
        }
        catch
        {
            // Reporting must remain outside the product failure boundary.
        }
    }

    public static void Report(string detail, string category)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return;
        }

        try
        {
            Volatile.Read(ref _messageSink)?.Invoke(detail, category);
        }
        catch
        {
            // Reporting must remain outside the product failure boundary.
        }
    }
}
