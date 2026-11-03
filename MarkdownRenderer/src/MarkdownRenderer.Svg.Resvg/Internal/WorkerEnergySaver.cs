using System.Runtime.InteropServices;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Reads the Windows energy-saver state that gates the optional secondary
/// worker, and classifies which query failures may fall back to one worker.
/// </summary>
internal sealed class WorkerEnergySaver(Func<bool> isEnergySaverOn)
{
    private readonly Func<bool> _isEnergySaverOn = isEnergySaverOn;

    /// <summary>Reads energy saver; a recoverable query failure reports it on.</summary>
    /// <param name="queryFailed">Set when the query failed recoverably.</param>
    /// <returns>Whether energy saver is on, or true after a recoverable failure.</returns>
    internal bool IsOn(out bool queryFailed)
    {
        try
        {
            queryFailed = false;
            return _isEnergySaverOn();
        }
        catch (Exception exception) when (IsRecoverablePowerQueryFailure(exception))
        {
            // Power state is an optimization gate, not a prerequisite for
            // rendering. If a recoverable platform/query failure occurs, keep
            // the pool on one worker for this scheduling pass. This also keeps
            // the failed PendingWork queued/owned by AcquireAsync instead of
            // letting a query exception escape after enqueue and strand it.
            queryFailed = true;
            return true;
        }
    }

    /// <summary>Whether this machine and its current power state allow the secondary worker.</summary>
    /// <returns>True when the secondary worker may run.</returns>
    internal bool AllowsSecondary()
    {
        bool energySaverOn = IsOn(out bool energySaverQueryFailed);
        return WorkerSchedulingPolicy.CanUseSecondary(
            RuntimeInformation.ProcessArchitecture,
            Environment.ProcessorCount,
            energySaverOn,
            energySaverQueryFailed);
    }

    internal static bool IsRecoverablePowerQueryFailure(Exception exception)
    {
        // Do not hide fatal resource/process failures behind the conservative
        // single-worker fallback. Check aggregate children too because a
        // platform query wrapper may report a fatal inner exception.
        if (exception is OutOfMemoryException or InsufficientMemoryException or
            InsufficientExecutionStackException or StackOverflowException or
            AccessViolationException or SEHException)
        {
            return false;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (Exception innerException in aggregate.InnerExceptions)
            {
                if (!IsRecoverablePowerQueryFailure(innerException))
                    return false;
            }

            return true;
        }

        return exception.InnerException is null ||
            IsRecoverablePowerQueryFailure(exception.InnerException);
    }

    internal static bool QueryEnergySaverOn() =>
        Windows.System.Power.PowerManager.EnergySaverStatus ==
        Windows.System.Power.EnergySaverStatus.On;
}
