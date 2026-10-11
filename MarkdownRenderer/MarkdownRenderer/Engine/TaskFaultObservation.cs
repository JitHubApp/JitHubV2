using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer;

/// <summary>Observes detached task failures without changing their owning lifetime.</summary>
internal static class TaskFaultObservation
{
    internal static void ObserveFault(Task task)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted)
                _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
