using BenchmarkDotNet.Attributes;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Benchmarks;

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
