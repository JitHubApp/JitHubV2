using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Controls.Highlighting;

/// <summary>
/// Tracks the highlight tasks that have started and not yet finished, so
/// retiring a generation can wait for every one of them. Tasks are added on
/// the UI thread and removed by their own completion on any thread, so the
/// set has its own lock.
/// </summary>
internal sealed class CodeBlockHighlightTasks
{
    private readonly object _tasksGate = new();
    private readonly HashSet<Task> _tasks = new();

    /// <summary>Tracks <paramref name="task"/> until it completes.</summary>
    internal void Track(Task task)
    {
        lock (_tasksGate)
        {
            _tasks.Add(task);
        }

        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var owner = (CodeBlockHighlightTasks)state!;
                lock (owner._tasksGate)
                {
                    owner._tasks.Remove(completed);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Stops tracking every current task and returns a task that completes
    /// when all of them have.
    /// </summary>
    internal Task Drain()
    {
        Task[] tasks;
        lock (_tasksGate)
        {
            if (_tasks.Count == 0)
                return Task.CompletedTask;

            tasks = new Task[_tasks.Count];
            _tasks.CopyTo(tasks);
            _tasks.Clear();
        }

        return Task.WhenAll(tasks);
    }
}
