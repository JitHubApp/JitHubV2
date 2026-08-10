using System.Text.Json;
using System.Text.Json.Serialization;
using JitHub.Services;

internal static class ProductPerformanceGateProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static int Main(string[] args)
    {
        try
        {
            ProductPerformanceGateOptions options = ProductPerformanceGateOptions.Parse(args);
            return options.Command switch
            {
                "plan" => WritePlan(options),
                "gate" => GateExistingReport(options),
                _ => RunBenchmark(options)
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Product performance gate failed: {exception}");
            return 2;
        }
    }

    private static int WritePlan(ProductPerformanceGateOptions options)
    {
        ProductPerformanceRunPlan plan = ProductPerformanceRunPlan.Create(
            options.Iterations,
            options.Fixtures,
            options.Routes);
        string fullPath = options.OutputPath;
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        WriteJsonAtomic(fullPath, plan);
        Console.WriteLine($"Wrote {plan.Cases.Count} canonical performance cases to {fullPath}.");
        return 0;
    }

    private static int GateExistingReport(ProductPerformanceGateOptions options)
    {
        ProductPerformanceReportDocument input = ProductPerformanceReportDocument.Read(options.OutputPath);
        ProductPerformanceReportDocument report = ProductPerformanceReportDocument.Create(
            input.Configuration,
            input.Measurements,
            options.Fixtures,
            options.Routes ?? input.EvaluatedRoutes,
            input.CreatedAt);
        report.WriteAtomic(options.OutputPath);
        return PrintResult(report);
    }

    private static int RunBenchmark(ProductPerformanceGateOptions options)
    {
        ProductPerformanceRunPlan plan = ProductPerformanceRunPlan.Create(
            options.Iterations,
            options.Fixtures,
            options.Routes);
        string dataRoot = Path.Combine(
            options.ArtifactsPath,
            $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}");
        Directory.CreateDirectory(dataRoot);
        ProductPerformanceRouteProbe probe = new(options.AppPath!, dataRoot, options.Repository);
        List<ProductPerformanceMeasurement> measurements = [];
        DateTimeOffset runStartedAt = DateTimeOffset.UtcNow;
        ProductPerformanceReportDocument initialReport = ProductPerformanceReportDocument.Create(
            options.Configuration,
            measurements,
            options.Fixtures,
            options.Routes,
            runStartedAt);
        initialReport.WriteAtomic(options.OutputPath);

        foreach (ProductPerformanceRunCase runCase in plan.Cases)
        {
            if (runCase.Fixture != ProductPerformanceFixture.Cold && runCase.Iteration == 0)
            {
                Console.WriteLine($"[{runCase.Fixture}] {runCase.Route.Id} cache warm-up");
                _ = probe.Run(runCase.CreateWarmup());
            }

            Console.WriteLine(
                $"[{runCase.Fixture}] {runCase.Route.Id} iteration {runCase.Iteration + 1}/{plan.Iterations}");
            measurements.AddRange(probe.Run(runCase));
            ProductPerformanceReportDocument checkpoint = ProductPerformanceReportDocument.Create(
                options.Configuration,
                measurements,
                options.Fixtures,
                options.Routes,
                runStartedAt);
            checkpoint.WriteAtomic(options.OutputPath);
        }

        ProductPerformanceReportDocument report = ProductPerformanceReportDocument.Create(
            options.Configuration,
            measurements,
            options.Fixtures,
            options.Routes,
            runStartedAt);
        report.WriteAtomic(options.OutputPath);
        return PrintResult(report);
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The output path must have a parent directory.", nameof(path));
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
                JsonSerializer.Serialize(stream, value, JsonOptions);
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

    private static int PrintResult(ProductPerformanceReportDocument report)
    {
        if (report.Gate.Passed)
        {
            Console.WriteLine(
                $"PASS: {report.Gate.Evaluations.Count} route/fixture/metric performance budgets passed.");
            return 0;
        }

        Console.Error.WriteLine(
            $"FAIL: {report.Gate.Failures.Count} of {report.Gate.Evaluations.Count} performance budgets failed.");
        foreach (ProductPerformanceRouteEvaluation failure in report.Gate.Failures)
        {
            Console.Error.WriteLine(
                $"  {failure.Evaluation.Budget.Fixture}/{failure.Route}/{failure.Evaluation.Budget.Metric}: " +
                failure.Evaluation.Detail);
        }

        return 1;
    }
}
