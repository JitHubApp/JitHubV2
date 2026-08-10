using System.Text.Json;
using System.Text.Json.Serialization;
using JitHub.Services;

internal sealed record ProductPerformanceReport(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string Configuration,
    string Framework,
    string OperatingSystem,
    IReadOnlyList<ProductPerformanceMeasurement> Measurements,
    ProductPerformanceGateResult Gate)
{
    public const int CurrentSchemaVersion = 2;

    public static ProductPerformanceReport Create(
        string? configuration,
        IEnumerable<ProductPerformanceMeasurement> measurements,
        IEnumerable<ProductPerformanceFixture>? fixtures = null)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        ProductPerformanceMeasurement[] snapshot = measurements
            .OrderBy(static measurement => measurement.Fixture)
            .ThenBy(static measurement => measurement.Route, StringComparer.Ordinal)
            .ThenBy(static measurement => measurement.Metric)
            .ThenBy(static measurement => measurement.RecordedAt)
            .ThenBy(static measurement => measurement.Value)
            .ToArray();
        return new ProductPerformanceReport(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration.Trim(),
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            snapshot,
            ProductPerformanceGate.Evaluate(snapshot, fixtures));
    }

    public void Write(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The report path must have a parent directory.", nameof(path));
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

    public static ProductPerformanceReport Read(string path)
    {
        using FileStream stream = File.OpenRead(Path.GetFullPath(path));
        ProductPerformanceReport? report = JsonSerializer.Deserialize<ProductPerformanceReport>(
            stream,
            SerializerOptions);
        if (report is null || report.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException("The product performance report is missing or uses an unsupported schema.");
        }

        if (report.Measurements is null || report.Gate is null)
        {
            throw new InvalidDataException("The product performance report is incomplete.");
        }

        return report;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
