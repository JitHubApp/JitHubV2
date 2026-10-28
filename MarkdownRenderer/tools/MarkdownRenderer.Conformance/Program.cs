using System.Text.Json;
using MarkdownRenderer.Conformance;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] args)
{
    try
    {
        var options = CommandLineOptions.Parse(args);
        if (options.ShowHelp)
        {
            CommandLineOptions.WriteHelp();
            return 0;
        }

        var definitions = options.Suite switch
        {
            "all" => CorpusCatalog.All,
            _ => Array.AsReadOnly([CorpusCatalog.Resolve(options.Suite)]),
        };
        var allowlist = ConformanceExceptionAllowlist.Load(options.AllowlistPath);
        var store = new CorpusStore();
        var results = new List<ConformanceResult>();
        var corpora = new List<CorpusEvidence>();
        var runner = new ConformanceRunner();

        foreach (var definition in definitions)
        {
            Console.WriteLine($"Loading {definition.Id} from pinned source {definition.SourceUri}...");
            var examples = await store.LoadAsync(
                definition,
                options.CorpusDirectory,
                options.Offline,
                CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"Running {examples.Count} examples with {definition.Profile.Id}...");
            results.AddRange(await runner.RunAsync(
                definition,
                examples,
                allowlist,
                CancellationToken.None).ConfigureAwait(false));
            corpora.Add(new CorpusEvidence(
                definition.Id,
                definition.SpecificationVersion,
                definition.SpecificationUri.ToString(),
                definition.SourceUri.ToString(),
                definition.SourceSha256,
                examples.Count));
        }

        var ordered = results
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

        string? reportDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ReportPath));
        if (!string.IsNullOrEmpty(reportDirectory))
            Directory.CreateDirectory(reportDirectory);
        await File.WriteAllBytesAsync(
            options.ReportPath,
            JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            })).ConfigureAwait(false);

        Console.WriteLine(ConformanceRunner.Limitation);
        Console.WriteLine(
            $"Total {summary.Total}: PASS {summary.Passed}, EXCEPTION {summary.Excepted}, FAIL {summary.Failed}");
        Console.WriteLine($"Report: {Path.GetFullPath(options.ReportPath)}");
        foreach (var failure in ordered.Where(static value => value.Status != ConformanceStatus.Pass).Take(25))
            Console.WriteLine($"{failure.Status}: {failure.Suite} #{failure.Example}: {failure.Failure} {failure.Issue}");
        return summary.Failed == 0 ? 0 : 1;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
}

internal sealed record CommandLineOptions(
    string Suite,
    string CorpusDirectory,
    string AllowlistPath,
    string ReportPath,
    bool Offline,
    bool ShowHelp)
{
    internal static CommandLineOptions Parse(string[] args)
    {
        string suite = "all";
        string corpusDirectory = Path.Combine("artifacts", "conformance", "corpora");
        string allowlist = Path.Combine(AppContext.BaseDirectory, "conformance-exceptions.v1.json");
        string report = Path.Combine("artifacts", "conformance", "report.json");
        bool offline = false;
        bool help = false;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--suite":
                    suite = ReadValue(args, ref index, argument);
                    break;
                case "--corpus-dir":
                    corpusDirectory = ReadValue(args, ref index, argument);
                    break;
                case "--allowlist":
                    allowlist = ReadValue(args, ref index, argument);
                    break;
                case "--report":
                    report = ReadValue(args, ref index, argument);
                    break;
                case "--offline":
                    offline = true;
                    break;
                case "--help" or "-h":
                    help = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{argument}'. Use --help for usage.");
            }
        }

        return new CommandLineOptions(suite, corpusDirectory, allowlist, report, offline, help);
    }

    internal static void WriteHelp()
    {
        Console.WriteLine("MarkdownRenderer official-example semantic conformance harness");
        Console.WriteLine("  --suite all|commonmark-0.31.2|gfm-0.29");
        Console.WriteLine("  --corpus-dir <path>   Verified corpus cache");
        Console.WriteLine("  --allowlist <path>    Versioned issue-linked exceptions JSON");
        Console.WriteLine("  --report <path>       Machine-readable result JSON");
        Console.WriteLine("  --offline             Refuse network access; require verified cache");
    }

    private static string ReadValue(string[] args, ref int index, string argument)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"{argument} requires a value.");
        return args[index];
    }
}
