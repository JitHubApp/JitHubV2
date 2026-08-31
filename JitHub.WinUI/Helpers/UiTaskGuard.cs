using System;
using System.Threading.Tasks;

namespace JitHub.WinUI.Helpers;

/// <summary>
/// Owns asynchronous work started from a synchronous XAML event boundary.
/// </summary>
internal static class UiTaskGuard
{
    public static void Run(Func<Task> operation, string category, Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        Task task;
        try
        {
            task = operation() ?? Task.CompletedTask;
        }
        catch (Exception exception)
        {
            ReportFailure(exception, category, onFailure);
            return;
        }

        _ = ObserveAsync(task, category, onFailure);
    }

    public static void Observe(Task task, string category, Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        _ = ObserveAsync(task, category, onFailure);
    }

    private static async Task ObserveAsync(
        Task task,
        string category,
        Action<Exception>? onFailure)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Navigation, unload, replacement requests, and dismissed dialogs all
            // use cancellation as their normal UI-lifetime completion signal.
        }
        catch (Exception exception)
        {
            ReportFailure(exception, category, onFailure);
        }
    }

    private static void ReportFailure(
        Exception exception,
        string category,
        Action<Exception>? onFailure)
    {
        App.LogHandledException(exception, category);
        try
        {
            onFailure?.Invoke(exception);
        }
        catch (Exception recoveryException)
        {
            App.LogHandledException(
                new AggregateException("A UI failure callback also failed.", exception, recoveryException),
                category + "-recovery");
        }
    }
}
