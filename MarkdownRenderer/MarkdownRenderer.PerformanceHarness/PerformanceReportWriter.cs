using System.Text.Json;

namespace MarkdownRenderer.PerformanceHarness;

internal static class PerformanceReportWriter
{
    internal static void WriteNew(string path, PerformanceReport report)
    {
        using FileStream stream = CreateNew(path, asynchronous: false);
        JsonSerializer.Serialize(stream, report, PerformanceReport.JsonOptions);
        stream.Flush(flushToDisk: true);
    }

    internal static async Task WriteNewAsync(
        string path,
        PerformanceReport report,
        CancellationToken cancellationToken = default)
    {
        await using FileStream stream = CreateNew(path, asynchronous: true);
        await JsonSerializer.SerializeAsync(
            stream,
            report,
            PerformanceReport.JsonOptions,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static FileStream CreateNew(string path, bool asynchronous)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Output report must have a parent directory.");

        Directory.CreateDirectory(directory);
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.SequentialScan |
                    (asynchronous ? FileOptions.Asynchronous : FileOptions.None),
            });
    }
}
