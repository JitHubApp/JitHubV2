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

[MemoryDiagnoser]
public class SourceMapBenchmarks
{
    private const int MappingCount = 100_000;
    private MarkdownSourceMap _sourceMap = null!;
    private DocumentRange[] _queries = null!;
    private int _queryIndex;

    [GlobalSetup]
    public void Setup()
    {
        _sourceMap = new MarkdownSourceMap(new string('x', MappingCount * 4));
        _queries = new DocumentRange[1024];

        for (int index = 0; index < MappingCount; index++)
        {
            int block = index + 1;
            _sourceMap.Add(block, 0, 3, new SourceSpan(index * 4, 3));
        }

        var random = new Random(0x4D_44);
        for (int index = 0; index < _queries.Length; index++)
        {
            int block = random.Next(1, MappingCount + 1);
            _queries[index] = new DocumentRange(
                new DocumentPosition(block, 0, 0),
                new DocumentPosition(block, 0, 3));
        }

        _sourceMap.TryMapRange(_queries[0], out _);
    }

    [Benchmark]
    public SourceSpan LookupAmongOneHundredThousandMappings()
    {
        var query = _queries[_queryIndex++ & (_queries.Length - 1)];
        return _sourceMap.TryMapRange(query, out var span) ? span : SourceSpan.Empty;
    }
}
