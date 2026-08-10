using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JitHub.Services;

public sealed record ProductPerformanceReportDocument(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string Configuration,
    string Framework,
    string OperatingSystem,
    IReadOnlyList<string> EvaluatedRoutes,
    IReadOnlyList<ProductPerformanceMeasurement> Measurements,
    ProductPerformanceGateResult Gate)
{
    public const int CurrentSchemaVersion = 3;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ProductPerformanceReportDocument Create(
        string? configuration,
        IEnumerable<ProductPerformanceMeasurement> measurements,
        IEnumerable<ProductPerformanceFixture>? fixtures = null,
        IEnumerable<string>? routes = null,
        DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        ProductPerformanceMeasurement[] snapshot = measurements
            .OrderBy(static measurement => measurement.Fixture)
            .ThenBy(static measurement => measurement.Route, StringComparer.Ordinal)
            .ThenBy(static measurement => measurement.Metric)
            .ThenBy(static measurement => measurement.RecordedAt)
            .ThenBy(static measurement => measurement.Value)
            .ToArray();

        string[] evaluatedRoutes = (routes ?? ProductPerformanceGate.Routes.Select(static route => route.Id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static route => route, StringComparer.Ordinal)
            .ToArray();

        return new ProductPerformanceReportDocument(
            CurrentSchemaVersion,
            createdAt ?? DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration.Trim(),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            evaluatedRoutes,
            snapshot,
            ProductPerformanceGate.Evaluate(snapshot, fixtures, evaluatedRoutes));
    }

    public void WriteAtomic(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("The report path must have a parent directory.", nameof(path));
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, this, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static ProductPerformanceReportDocument Read(string path)
    {
        using FileStream stream = File.OpenRead(Path.GetFullPath(path));
        ProductPerformanceReportDocument? report = JsonSerializer.Deserialize<ProductPerformanceReportDocument>(
            stream,
            SerializerOptions);
        if (report is null || report.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException("The product performance report is missing or uses an unsupported schema.");
        }

        if (report.Measurements is null || report.Gate is null || report.EvaluatedRoutes is null)
        {
            throw new InvalidDataException("The product performance report is incomplete.");
        }

        return report;
    }
}
