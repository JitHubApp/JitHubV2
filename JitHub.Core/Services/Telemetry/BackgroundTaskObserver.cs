using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

/// <summary>
/// Contains optional view-model and service work that intentionally outlives its caller.
/// </summary>
public static class BackgroundTaskObserver
{
    public static void MarkFaultObserved(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _ = ObserveFaultAsync(task);
    }

    public static void Run(
        Func<Task> operation,
        string feature,
        ITelemetryService telemetryService,
        Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        ArgumentNullException.ThrowIfNull(telemetryService);

        Task task;
        try
        {
            task = operation() ?? Task.CompletedTask;
        }
        catch (Exception exception)
        {
            ReportFailure(exception, feature, telemetryService, onFailure);
            return;
        }

        _ = ObserveAsync(task, feature, telemetryService, onFailure);
    }

    public static async Task ObserveAsync(
        Task task,
        string feature,
        ITelemetryService telemetryService,
        Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        ArgumentNullException.ThrowIfNull(telemetryService);

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Replacement, account, navigation, and shutdown cancellation are normal.
        }
        catch (Exception exception)
        {
            ReportFailure(exception, feature, telemetryService, onFailure);
        }
    }

    private static async Task ObserveFaultAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private static void ReportFailure(
        Exception exception,
        string feature,
        ITelemetryService telemetryService,
        Action<Exception>? onFailure)
    {
        ITelemetryService safeTelemetry = SafeTelemetryService.Wrap(telemetryService);
        safeTelemetry.TrackEvent(
            "app.background_task.failed",
            new Dictionary<string, string?>
            {
                ["feature"] = feature,
                ["error_kind"] = exception.GetBaseException().GetType().Name,
                ["phase"] = TelemetryTaxonomy.Sources.Background
            });

        try
        {
            onFailure?.Invoke(exception);
        }
        catch (Exception recoveryException)
        {
            safeTelemetry.TrackEvent(
                "app.background_task.failed",
                new Dictionary<string, string?>
                {
                    ["feature"] = feature,
                    ["error_kind"] = recoveryException.GetBaseException().GetType().Name,
                    ["phase"] = TelemetryTaxonomy.Sources.Background
                });
        }
    }
}
