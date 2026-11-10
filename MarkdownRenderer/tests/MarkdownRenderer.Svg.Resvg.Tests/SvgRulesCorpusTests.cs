using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal.Preflight;
using MarkdownRenderer.Svg.Resvg.Tests.TestSupport;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// Runs the shared SVG rules corpus (<c>MarkdownRenderer/tests/corpus/svg-rules</c>)
/// against the host's two rule implementations: source preparation
/// (<see cref="ResvgMarkdownSvgRenderer.PrepareSource"/>, the <c>host</c> column) and the
/// direct open preflight (<c>preflight</c>). The worker crate checks the <c>worker</c>
/// column against the same manifest. Every layer sees the identical authored bytes.
/// </summary>
/// <remarks>
/// Source preparation runs under the resource-budget wall-clock deadline, so this
/// class shares the deadline-sensitive collection rather than racing worker tests.
/// </remarks>
[Collection(WorkerDeadlineSensitiveCollection.Name)]
public sealed class SvgRulesCorpusTests
{
    private static readonly string[] VerdictKeys = ["worker", "host", "preflight"];
    private static readonly string[] KnownKeys = ["input", "seed", "worker", "host", "preflight", "difference", "note"];

    [Fact]
    public void HostSourcePreparationMatchesTheSharedCorpus()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        var failures = new List<string>();
        foreach (CorpusCase corpusCase in LoadCases())
        {
            byte[] source = corpusCase.ReadInput();
            MarkdownSvgSourcePreparation preparation = renderer.PrepareSource(source, CancellationToken.None);
            string actual = preparation.SanitizedBytes is not null
                ? "accept"
                : $"reject {preparation.FailureReason}: {HostReason(preparation.FailureDescription)}";
            Expect(corpusCase, "host", actual, failures);
        }

        Assert.True(failures.Count == 0, Report(failures));
    }

    [Fact]
    public async Task DirectOpenPreflightMatchesTheSharedCorpus()
    {
        var options = new ResvgMarkdownSvgRendererOptions();
        var failures = new List<string>();
        foreach (CorpusCase corpusCase in LoadCases())
        {
            byte[] source = corpusCase.ReadInput();
            string actual;
            try
            {
                SvgPreflightResult result = await SvgPreflight.InspectCoreAsync(source, options, CancellationToken.None);
                result.ReleaseSource();
                actual = "accept";
            }
            catch (MarkdownSvgException exception)
            {
                actual = $"reject {exception.Reason}: {exception.Message}";
            }

            Expect(corpusCase, "preflight", actual, failures);
        }

        Assert.True(failures.Count == 0, Report(failures));
    }

    [Fact]
    public void EveryCrossSideDisagreementIsRecordedAsAnExpectedDifference()
    {
        var failures = new List<string>();
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (CorpusCase corpusCase in LoadCases())
        {
            referenced.Add(corpusCase.Input);
            bool[] accepts = VerdictKeys
                .Select(key => corpusCase.Field(key))
                .OfType<string>()
                .Select(verdict => verdict == "accept")
                .ToArray();
            if (accepts.Length != VerdictKeys.Length)
                failures.Add($"{corpusCase.Context}: every case needs worker, host and preflight verdicts.");

            bool disagree = accepts.Distinct().Count() > 1;
            bool recorded = corpusCase.Field("difference") is not null;
            if (disagree && !recorded)
                failures.Add($"{corpusCase.Context}: sides disagree on accept/reject but no 'difference' explains why.");
            if (!disagree && recorded)
                failures.Add($"{corpusCase.Context}: 'difference' is recorded but every side agrees.");
        }

        foreach (string input in Directory.EnumerateFiles(Path.Combine(CorpusRoot, "inputs")).Select(Path.GetFileName).OfType<string>())
        {
            if (!referenced.Contains(input))
                failures.Add($"Input '{input}' is not referenced by any case.");
        }

        Assert.True(failures.Count == 0, Report(failures));
    }

    private static void Expect(CorpusCase corpusCase, string key, string actual, List<string> failures)
    {
        string? expected = corpusCase.Field(key);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            failures.Add($"{corpusCase.Context}: {key} expected '{expected ?? "<missing>"}' but was '{actual}'.");
    }

    private static string HostReason(string? description)
    {
        // RejectPreparedSource formats every host rejection as this sentence.
        const string Prefix = "The SVG was rejected by host preflight (";
        const string Suffix = ").";
        if (description is null)
            return "<no description>";
        return description.StartsWith(Prefix, StringComparison.Ordinal) && description.EndsWith(Suffix, StringComparison.Ordinal)
            ? description[Prefix.Length..^Suffix.Length]
            : description;
    }

    private static string Report(List<string> failures) =>
        $"{failures.Count} corpus failure(s):{Environment.NewLine}{string.Join(Environment.NewLine, failures)}";

    private static string CorpusRoot { get; } = FindCorpusRoot();

    private static string FindCorpusRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "MarkdownRenderer", "tests", "corpus", "svg-rules");
            if (File.Exists(Path.Combine(candidate, "manifest.txt")))
                return candidate;
        }

        throw new DirectoryNotFoundException("The shared SVG rules corpus was not found above the test output directory.");
    }

    private static List<CorpusCase> LoadCases()
    {
        var cases = new List<CorpusCase>();
        string[] lines = File.ReadAllLines(Path.Combine(CorpusRoot, "manifest.txt"), Encoding.UTF8);
        for (int index = 0; index < lines.Length; index++)
        {
            int lineNumber = index + 1;
            string line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith('['))
            {
                Assert.True(line.EndsWith(']'), $"Manifest line {lineNumber}: unterminated case header.");
                string id = line[1..^1];
                Assert.True(cases.TrueForAll(existing => existing.Id != id), $"Manifest line {lineNumber}: duplicate case '{id}'.");
                cases.Add(new CorpusCase(id, lineNumber));
                continue;
            }

            int separator = line.IndexOf('=', StringComparison.Ordinal);
            Assert.True(separator > 0, $"Manifest line {lineNumber}: expected 'key = value'.");
            string key = line[..separator].Trim();
            Assert.Contains(key, KnownKeys);
            Assert.True(cases.Count > 0, $"Manifest line {lineNumber}: field before the first case.");
            Assert.True(
                cases[^1].Fields.TryAdd(key, line[(separator + 1)..].Trim()),
                $"Manifest line {lineNumber}: duplicate key '{key}'.");
        }

        Assert.NotEmpty(cases);
        return cases;
    }

    private sealed class CorpusCase(string id, int line)
    {
        public string Id { get; } = id;

        public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);

        public string Context => $"[{Id}] (manifest line {line})";

        public string Input => Field("input") ?? throw new InvalidDataException($"{Context}: missing 'input'.");

        public string? Field(string key) => Fields.TryGetValue(key, out string? value) ? value : null;

        public byte[] ReadInput() => File.ReadAllBytes(Path.Combine(CorpusRoot, "inputs", Input));
    }
}
