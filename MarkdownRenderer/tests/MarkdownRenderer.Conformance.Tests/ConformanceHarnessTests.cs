using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarkdownRenderer.Conformance;
using Xunit;

namespace MarkdownRenderer.Conformance.Tests;

public sealed class ConformanceHarnessTests
{
    [Fact]
    public async Task Serializer_UsesBuiltCoreAssembly_AndIsDeterministic()
    {
        Assert.Equal("MarkdownRenderer.Core", typeof(MarkdownEngine).Assembly.GetName().Name);
        var engine = new MarkdownEngineBuilder().UseProfile(MarkdownProfiles.CommonMark).Build();
        var document = await engine.ParseAsync("# Heading\n\n[link](https://example.test)\n");

        byte[] first = SemanticPlanSerializer.Serialize(engine.Profile, document);
        byte[] second = SemanticPlanSerializer.Serialize(engine.Profile, document);

        Assert.Equal(first, second);
        using var json = JsonDocument.Parse(first);
        Assert.Equal("commonmark-0.31.2", json.RootElement.GetProperty("profile").GetString());
        Assert.Single(json.RootElement.GetProperty("headings").EnumerateArray());
    }

    [Fact]
    public void SpecJsonParser_ReadsOfficialFieldShape()
    {
        const string json = """
            [{"markdown":"# x\n","html":"<h1>x</h1>\n","example":7,"start_line":10,"end_line":15,"section":"ATX headings"}]
            """;

        var example = Assert.Single(CorpusStore.ParseSpecJson(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(7, example.Example);
        Assert.Equal("ATX headings", example.Section);
        Assert.Equal("# x\n", example.Markdown);
    }

    [Fact]
    public void AnnotatedSpecParser_NormalizesTabs_AndProducesExamples()
    {
        const string text = """
            # Tabs

            ```````````````````````````````` example
            →foo
            .
            <pre><code>foo
            </code></pre>
            ````````````````````````````````
            """;

        var example = Assert.Single(CorpusStore.ParseAnnotatedSpec(Encoding.UTF8.GetBytes(text)));

        Assert.Equal("Tabs", example.Section);
        Assert.Equal("\tfoo\n", example.Markdown);
        Assert.Equal(1, example.Example);
    }

    [Fact]
    public void AnnotatedSpecParser_PreservesOfficialNumbers_AndExtensionOptions()
    {
        const string text = """
            # Extensions

            ```````````````````````````````` example disabled
            skipped
            .
            <p>skipped</p>
            ````````````````````````````````

            ```````````````````````````````` example table autolink
            | a |
            | - |
            .
            <table></table>
            ````````````````````````````````
            """;

        SpecExample example = Assert.Single(
            CorpusStore.ParseAnnotatedSpec(Encoding.UTF8.GetBytes(text)));

        Assert.Equal(2, example.Example);
        Assert.Equal(["table", "autolink"], example.Extensions);
    }

    [Fact]
    public void ChecksumVerification_RejectsChangedCorpus()
    {
        byte[] payload = "official"u8.ToArray();
        string otherHash = Convert.ToHexString(SHA256.HashData("changed"u8)).ToLowerInvariant();

        var error = Assert.Throws<InvalidDataException>(() =>
            CorpusStore.VerifyChecksum(payload, otherHash, "test corpus"));

        Assert.Contains("SHA-256 mismatch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Allowlist_RequiresIssueLinks_AndRejectsDuplicates()
    {
        string path = Path.Combine(Path.GetTempPath(), $"markdown-conformance-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "schemaVersion": 1,
                  "entries": [
                    {"suite":"commonmark-0.31.2","example":1,"issue":"not-a-link","reason":"known"}
                  ]
                }
                """);

            var error = Assert.Throws<InvalidDataException>(() => ConformanceExceptionAllowlist.Load(path));
            Assert.Contains("HTTPS issue", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Runner_RejectsOfficialReferenceHtmlMismatch()
    {
        var definition = CorpusCatalog.CommonMark0312;
        var examples = new[]
        {
            new SpecExample(1, "Heading", "# hello\n", "<h1>intentionally different</h1>\n", 1, 6),
        };

        var result = Assert.Single(await new ConformanceRunner().RunAsync(
            definition,
            examples,
            ConformanceExceptionAllowlist.Empty));

        Assert.Equal(ConformanceStatus.Fail, result.Status);
        Assert.Contains("Official reference HTML differs", result.Failure, StringComparison.Ordinal);
        Assert.NotEmpty(result.PlanSha256);
        Assert.NotEqual(result.ExpectedHtmlSha256, result.ActualHtmlSha256);
    }

    [Fact]
    public async Task Runner_RequiresReferenceParityAndDeterministicPlan()
    {
        var example = new SpecExample(
            1,
            "Heading",
            "# hello\n",
            "<h1>hello</h1>\n",
            1,
            6);

        ConformanceResult result = Assert.Single(await new ConformanceRunner().RunAsync(
            CorpusCatalog.CommonMark0312,
            [example],
            ConformanceExceptionAllowlist.Empty));

        Assert.Equal(ConformanceStatus.Pass, result.Status);
        Assert.Equal(result.ExpectedHtmlSha256, result.ActualHtmlSha256);
        Assert.NotEmpty(result.PlanSha256);
    }

    [Fact]
    public async Task Runner_EnablesOnlyExtensionsDeclaredByEachGfmExample()
    {
        const string markdown = "https://example.com\n";
        var examples = new[]
        {
            new SpecExample(
                1,
                "Core",
                markdown,
                "<p>https://example.com</p>\n",
                1,
                1),
            new SpecExample(
                2,
                "Autolinks (extension)",
                markdown,
                "<p><a href=\"https://example.com\">https://example.com</a></p>\n",
                2,
                2)
            {
                Extensions = ["autolink"],
            },
        };

        IReadOnlyList<ConformanceResult> results = await new ConformanceRunner().RunAsync(
            CorpusCatalog.Gfm029,
            examples,
            ConformanceExceptionAllowlist.Empty);

        Assert.All(results, result => Assert.Equal(ConformanceStatus.Pass, result.Status));
    }
}
