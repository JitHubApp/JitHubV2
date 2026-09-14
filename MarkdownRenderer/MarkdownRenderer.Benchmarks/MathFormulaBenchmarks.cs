using BenchmarkDotNet.Attributes;
using MarkdownRenderer.Math;

namespace MarkdownRenderer.Benchmarks;

public enum MathFormulaCorpus
{
    Inline,
    Display,
}

/// <summary>
/// Measures the complete UI-independent Math stage: TeX parse, typesetting,
/// outline extraction, and immutable vector-scene construction. Font loading is
/// warmed in setup so the benchmark represents reusable-engine operation.
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class MathFormulaBenchmarks
{
    private MathFormulaProcessor _processor = null!;
    private MathFormulaRequest _request = null!;

    [Params(MathFormulaCorpus.Inline, MathFormulaCorpus.Display)]
    public MathFormulaCorpus Corpus { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        string markdown = Corpus switch
        {
            MathFormulaCorpus.Inline => @"$E = mc^2$",
            MathFormulaCorpus.Display => @"$$\sum_{i=0}^{100}\frac{i^2 + \sqrt{i}}{i + 1}$$",
            _ => throw new ArgumentOutOfRangeException(),
        };

        _processor = new MathFormulaProcessor();
        _request = MathDelimiterScanner.Scan(markdown).Formulas.Single();
        MathFormulaResult warm = await _processor.ProcessAsync(_request);
        if (!warm.IsAccepted)
            throw new InvalidOperationException(warm.Diagnostic?.Message ?? "Math warmup failed.");
    }

    [Benchmark]
    public ValueTask<MathFormulaResult> ParseTypesetAndBuildVectorScene() =>
        _processor.ProcessAsync(_request);
}
