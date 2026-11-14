using System.Text.Json;
using MarkdownRenderer.Conformance.Tests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.Conformance.Tests;

/// <summary>
/// Runs every official CommonMark and GFM example. The run downloads the
/// pinned corpora on first use, so it is opt-in: set
/// <c>MARKDOWN_RENDERER_CONFORMANCE=1</c> to run it, and
/// <c>MARKDOWN_RENDERER_CONFORMANCE_OFFLINE=1</c> to require the verified cache.
/// </summary>
public sealed class OfficialCorpusConformanceTests(ITestOutputHelper output)
{
    private const string EnableVariable = "MARKDOWN_RENDERER_CONFORMANCE";
    private const string OfflineVariable = "MARKDOWN_RENDERER_CONFORMANCE_OFFLINE";

    [SkippableFact]
    [Trait("Category", "Conformance")]
    public async Task EveryOfficialExample_PassesOrIsAllowlisted()
    {
        Skip.IfNot(
            Environment.GetEnvironmentVariable(EnableVariable) == "1",
            $"Set {EnableVariable}=1 to run the official CommonMark and GFM corpora.");

        string artifacts = Path.Combine(FindRendererRoot(), "artifacts", "conformance");
        bool offline = Environment.GetEnvironmentVariable(OfflineVariable) == "1";
        var allowlist = ConformanceExceptionAllowlist.Load(
            Path.Combine(AppContext.BaseDirectory, "conformance-exceptions.v1.json"));
        var store = new CorpusStore();
        var runner = new ConformanceRunner();
        var results = new List<ConformanceResult>();
        var corpora = new List<CorpusEvidence>();

        foreach (CorpusDefinition definition in CorpusCatalog.All)
        {
            IReadOnlyList<SpecExample> examples = await store.LoadAsync(
                definition,
                Path.Combine(artifacts, "corpora"),
                offline,
                CancellationToken.None);
            results.AddRange(await runner.RunAsync(definition, examples, allowlist, CancellationToken.None));
            corpora.Add(new CorpusEvidence(
                definition.Id,
                definition.SpecificationVersion,
                definition.SpecificationUri.ToString(),
                definition.SourceUri.ToString(),
                definition.SourceSha256,
                examples.Count));
        }

        ConformanceResult[] ordered = results
            .OrderBy(static value => value.Suite, StringComparer.Ordinal)
            .ThenBy(static value => value.Example)
            .ToArray();
        var summary = new ConformanceSummary(
            ordered.Length,
            ordered.Count(static value => value.Status == ConformanceStatus.Pass),
            ordered.Count(static value => value.Status == ConformanceStatus.Exception),
            ordered.Count(static value => value.Status == ConformanceStatus.Fail));
        var report = new ConformanceReport(
            SchemaVersion: 1,
            ConformanceRunner.VerificationLevel,
            ConformanceRunner.Limitation,
            DateTimeOffset.UtcNow,
            corpora.AsReadOnly(),
            summary,
            Array.AsReadOnly(ordered));

        string reportPath = Path.Combine(artifacts, "report.json");
        Directory.CreateDirectory(artifacts);
        await File.WriteAllBytesAsync(
            reportPath,
            JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));

        output.WriteLine(ConformanceRunner.Limitation);
        output.WriteLine(
            $"Total {summary.Total}: PASS {summary.Passed}, EXCEPTION {summary.Excepted}, FAIL {summary.Failed}");
        output.WriteLine($"Report: {reportPath}");
        foreach (ConformanceResult failure in ordered.Where(static value => value.Status != ConformanceStatus.Pass).Take(25))
            output.WriteLine($"{failure.Status}: {failure.Suite} #{failure.Example}: {failure.Failure} {failure.Issue}");

        Assert.True(summary.Failed == 0, $"{summary.Failed} official examples failed; see {reportPath}.");
    }

    private static string FindRendererRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MarkdownRenderer.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("The MarkdownRenderer folder was not found above the test output.");
    }
}
