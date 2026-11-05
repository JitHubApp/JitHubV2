using BenchmarkDotNet.Attributes;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
public class MarkdownEngineBenchmarks
{
    private MarkdownEngine _coldEngine = null!;
    private MarkdownEngine _warmEngine = null!;
    private string _source = null!;

    [Params(100 * 1024, 1024 * 1024)]
    public int SourceBytes { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _source = CreateDocument(SourceBytes / sizeof(char));
        _coldEngine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .WithParseCacheBudgetBytes(0)
            .Build();
        _warmEngine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .WithParseCacheBudgetBytes(System.Math.Max(32L * 1024 * 1024, SourceBytes * 8L))
            .Build();
        _ = await _warmEngine.ParseAsync(_source).ConfigureAwait(false);
    }

    [Benchmark]
    public Task<MarkdownDocument> ParseWithoutCompletedCache()
        => _coldEngine.ParseAsync(_source);

    [Benchmark]
    public Task<MarkdownDocument> ReuseWarmDocument()
        => _warmEngine.ParseAsync(_source);

    private static string CreateDocument(int characterCount)
    {
        const string section =
            "## Native rendering\n\n" +
            "A paragraph with **strong text**, [a link](https://example.invalid), and `code`.\n\n" +
            "| Feature | State |\n|:--|--:|\n| Tables | Ready |\n\n" +
            "- [x] Semantic task\n- Nested item\n\n" +
            "```csharp\nConsole.WriteLine(\"WinUI\");\n```\n\n";

        var builder = new System.Text.StringBuilder(characterCount + section.Length);
        while (builder.Length < characterCount)
            builder.Append(section);
        if (builder.Length > characterCount)
            builder.Length = characterCount;
        return builder.ToString();
    }
}
