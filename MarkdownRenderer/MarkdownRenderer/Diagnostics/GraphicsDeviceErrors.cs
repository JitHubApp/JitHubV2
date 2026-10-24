using System;

namespace MarkdownRenderer.Diagnostics;

internal static class GraphicsDeviceErrors
{
    private const int MaximumExceptionVisits = 1024;
    private const int MaximumAggregateDepth = 32;
    public const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    public const int DxgiErrorDeviceHung = unchecked((int)0x887A0006);
    public const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);
    public const int DxgiErrorDriverInternalError = unchecked((int)0x887A0020);
    public const int D2DErrorRecreateTarget = unchecked((int)0x8899000C);
    public const int D3DErrorDeviceLost = unchecked((int)0x88760868);
    public const int D3DErrorDeviceNotReset = unchecked((int)0x88760869);
    public const int RpcErrorDisconnected = unchecked((int)0x80010108);
    public const int RpcErrorServerDied = unchecked((int)0x80010007);
    public const int RpcErrorServerDiedDne = unchecked((int)0x80010012);
    public const int RoErrorClosed = unchecked((int)0x80000013);
    public const int Win32ErrorInvalidHandle = unchecked((int)0x80070006);
    public const int Win32ErrorInvalidWindowHandle = unchecked((int)0x80070578);

    public static bool IsDeviceLost(Exception? exception)
    {
        int remaining = MaximumExceptionVisits;
        return MatchesFailure(exception, shutdown: false, aggregateDepth: 0, ref remaining);
    }

    public static bool IsDeviceLostHResult(int hresult)
        => hresult is DxgiErrorDeviceRemoved
            or DxgiErrorDeviceHung
            or DxgiErrorDeviceReset
            or DxgiErrorDriverInternalError
            or D2DErrorRecreateTarget
            or D3DErrorDeviceLost
            or D3DErrorDeviceNotReset;

    public static bool IsShutdownOrDisposed(Exception? exception)
    {
        int remaining = MaximumExceptionVisits;
        return MatchesFailure(exception, shutdown: true, aggregateDepth: 0, ref remaining);
    }

    private static bool MatchesFailure(
        Exception? exception, bool shutdown, int aggregateDepth, ref int remaining)
    {
        // AggregateException.InnerException exposes only its first child. A
        // paint failure and a failed native close can report device loss in a
        // later child, which must still drive device/shutdown recovery. Walk
        // chains without recursion, and bound aggregate nesting/visits without
        // allocating or flattening a potentially hostile shared exception DAG.
        for (Exception? current = exception; current is not null && remaining > 0;
             current = current.InnerException)
        {
            remaining--;
            if (MatchesCurrentFailure(current, shutdown))
            {
                return true;
            }
            if (current is not AggregateException aggregate)
                continue;
            if (aggregateDepth >= MaximumAggregateDepth)
                return false;
            // Inspect sibling failures before descending into one primary
            // branch. A large/shared primary graph must not exhaust the visit
            // budget and hide the direct cleanup/device failure beside it.
            for (int index = 0; index < aggregate.InnerExceptions.Count; index++)
            {
                if (remaining == 0)
                    return false;
                remaining--;
                if (MatchesCurrentFailure(aggregate.InnerExceptions[index], shutdown))
                    return true;
            }
            for (int index = 0; index < aggregate.InnerExceptions.Count; index++)
            {
                if (remaining == 0)
                    return false;
                // Share the bounded descent budget fairly across siblings.
                // The shallow prepass cannot see a cleanup failure wrapped in
                // another exception/aggregate. A hostile shared primary DAG
                // must not consume every visit before that sibling is reached.
                // Unused visits remain available to later siblings; no queue,
                // visited set, flattening or per-node allocation is needed.
                int budget = System.Math.Max(1, remaining / (aggregate.InnerExceptions.Count - index));
                int childRemaining = budget;
                bool matched = MatchesFailure(aggregate.InnerExceptions[index], shutdown,
                    aggregateDepth + 1, ref childRemaining);
                remaining -= budget - childRemaining;
                if (matched)
                    return true;
            }
            // The first child was visited above; do not scan its chain twice.
            return false;
        }
        return false;
    }

    private static bool MatchesCurrentFailure(Exception exception, bool shutdown) =>
        shutdown
            ? exception is ObjectDisposedException || IsShutdownHResult(exception.HResult)
            : IsDeviceLostHResult(exception.HResult);

    public static bool IsShutdownHResult(int hresult)
        => hresult is RpcErrorDisconnected
            or RpcErrorServerDied
            or RpcErrorServerDiedDne
            or RoErrorClosed
            or Win32ErrorInvalidHandle
            or Win32ErrorInvalidWindowHandle;

    public static string FormatHResult(int hresult)
        => $"0x{unchecked((uint)hresult):X8}";
}
