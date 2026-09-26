using BenchmarkDotNet.Attributes;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Benchmarks;

/// <summary>
/// Exercises the same immutable cumulative-height index used by nested lists,
/// table rows, and code-block visual lines during warm viewport operations.
/// </summary>
[MemoryDiagnoser]
public class VerticalViewportIndexBenchmarks
{
    private double[] _bottomEdges = null!;
    private double[] _queries = null!;
    private int _queryIndex;

    [Params(100_000, 1_000_000)]
    public int BandCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _bottomEdges = new double[BandCount];
        for (int index = 0; index < _bottomEdges.Length; index++)
            _bottomEdges[index] = (index + 1) * 18.25;

        _queries = new double[1024];
        var random = new Random(0x5649_4557);
        for (int index = 0; index < _queries.Length; index++)
            _queries[index] = random.NextDouble() * _bottomEdges[^1];

        _ = VerticalViewportIndex.FindFirstIntersecting(_bottomEdges, _queries[0]);
    }

    [Benchmark]
    public int LocateVisibleBand()
        => VerticalViewportIndex.FindFirstIntersecting(
            _bottomEdges,
            _queries[_queryIndex++ & (_queries.Length - 1)]);
}
