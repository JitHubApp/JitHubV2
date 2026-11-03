using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Reads the Windows energy-saver state that gates the optional secondary
/// worker, and classifies which query failures may fall back to one worker.
/// </summary>
internal static class WorkerEnergySaver
{
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
