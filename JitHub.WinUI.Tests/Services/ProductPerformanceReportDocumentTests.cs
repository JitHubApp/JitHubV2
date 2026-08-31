using System.Text.Json;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ProductPerformanceReportDocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"jithub-perf-{Guid.NewGuid():N}");

    [Fact]
    public void Report_IsMachineReadableStableAndRoundTrips()
    {
        Directory.CreateDirectory(_root);
        ProductPerformanceMeasurement[] measurements =
        [
            new(ProductPerformanceFixture.Warm, ProductPerformanceMetric.RouteToFirstDataContent, 50, "settings", DateTimeOffset.UnixEpoch.AddSeconds(2)),
            new(ProductPerformanceFixture.Warm, ProductPerformanceMetric.RouteToFirstDataContent, 40, "home", DateTimeOffset.UnixEpoch.AddSeconds(1))
        ];
        ProductPerformanceReportDocument report = ProductPerformanceReportDocument.Create(
            "Release",
            measurements,
            [ProductPerformanceFixture.Warm],
            ["settings", "home"],
            createdAt: DateTimeOffset.UnixEpoch);
        string path = Path.Combine(_root, "report.json");

        report.WriteAtomic(path);
        ProductPerformanceReportDocument restored = ProductPerformanceReportDocument.Read(path);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));

        Assert.Equal(ProductPerformanceReportDocument.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.Equal("Release", restored.Configuration);
        Assert.Equal("home", restored.Measurements[0].Route);
        Assert.Equal(new[] { "home", "settings" }, restored.EvaluatedRoutes);
        Assert.True(json.RootElement.TryGetProperty("measurements", out _));
        Assert.True(json.RootElement.TryGetProperty("gate", out _));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void AtomicWrite_ReplacesExistingReportAndCleansTemporaryFiles()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "report.json");
        File.WriteAllText(path, "old content");
        ProductPerformanceReportDocument report = ProductPerformanceReportDocument.Create(
            "Debug",
            [],
            [ProductPerformanceFixture.Warm],
            ["home"],
            createdAt: DateTimeOffset.UnixEpoch);

        report.WriteAtomic(path);

        Assert.DoesNotContain("old content", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Read_RejectsUnsupportedSchema()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "report.json");
        File.WriteAllText(path, "{\"schemaVersion\":999}");

        Assert.Throws<InvalidDataException>(() => ProductPerformanceReportDocument.Read(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
