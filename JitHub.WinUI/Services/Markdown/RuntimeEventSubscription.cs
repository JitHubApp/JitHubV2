using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JitHub.Services.Markdown;

/// <summary>
/// Makes optional Windows runtime event sources best-effort without weakening
/// exception handling in rendering or application logic.
/// </summary>
internal static class RuntimeEventSubscription
{
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
            Debug.WriteLine($"[MarkdownViewer] Runtime settings source '{sourceName}' is unavailable: 0x{exception.HResult:X8}.");
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
            Debug.WriteLine($"[MarkdownViewer] Runtime event '{eventName}' is unavailable: 0x{exception.HResult:X8}.");
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
            Debug.WriteLine($"[MarkdownViewer] Runtime event '{eventName}' could not be detached because its OS source is unavailable: 0x{exception.HResult:X8}.");
        }
    }
}
