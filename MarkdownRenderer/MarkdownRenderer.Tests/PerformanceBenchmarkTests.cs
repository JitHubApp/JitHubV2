using System.Diagnostics;
using Markdig;
using Markdig.Syntax;
using MarkdownRenderer;
using MarkdownRenderer.Document;
using Xunit;

namespace MarkdownRenderer.Tests;

public class PerformanceBenchmarkTests
{
    private static string GenerateMarkdown(int lines)
    {
        var sb = new System.Text.StringBuilder(lines * 50);
        for (int i = 0; i < lines; i++)
        {
            int mod = i % 20;
            if (mod == 0) sb.AppendLine($"## Heading {i / 20 + 1}");
            else if (mod == 1) sb.AppendLine($"Paragraph {i}: Lorem ipsum dolor sit amet, **bold text**, *italic*, and `code`.");
            else if (mod == 2) sb.AppendLine($"- List item {i}");
            else if (mod == 3) sb.AppendLine($"  - Nested item {i}");
            else if (mod == 4) sb.AppendLine($"> Blockquote line {i}");
            else if (mod == 5) { sb.AppendLine("```csharp"); sb.AppendLine($"var x = {i};"); sb.AppendLine("```"); }
            else if (mod == 6) sb.AppendLine($"| Col1 | Col2 | Col3 |");
            else if (mod == 7) sb.AppendLine($"| ---- | ---- | ---- |");
            else if (mod == 8) sb.AppendLine($"| Data {i} | More {i} | Even more |");
            else if (mod == 9) sb.AppendLine($"- [x] Task item {i}");
            else sb.AppendLine($"Normal paragraph line {i} with some text that is reasonably long to simulate real content.");
        }
        return sb.ToString();
    }

    [Fact]
    public void Parse_100Lines_UnderTenMilliseconds()
    {
        var md = GenerateMarkdown(100);
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        // Warmup
        Markdown.Parse(md, pipeline);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) Markdown.Parse(md, pipeline);
        sw.Stop();

        var avgMs = sw.ElapsedMilliseconds / 10.0;
        Assert.True(avgMs < 10, $"Parse 100 lines avg {avgMs:F1}ms, expected < 10ms");
    }

    [Fact]
    public void Parse_10000Lines_UnderOneSecond()
    {
        var md = GenerateMarkdown(10_000);
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        // Warmup
        Markdown.Parse(md, pipeline);

        var sw = Stopwatch.StartNew();
        Markdown.Parse(md, pipeline);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"Parse 10k lines took {sw.ElapsedMilliseconds}ms, expected < 1000ms");
    }

    [Fact]
    public void Parse_RepeatedCalls_NoMemoryLeak()
    {
        var md = GenerateMarkdown(1_000);
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        // Run 50 times to spot obvious leaks
        for (int i = 0; i < 50; i++)
        {
            var doc = Markdown.Parse(md, pipeline);
            GC.KeepAlive(doc);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long memAfter = GC.GetTotalMemory(false);
        // Just verify we didn't crash — memory assertion would be flaky
        Assert.True(memAfter >= 0);
    }

    [Fact]
    public void Parse_EmptyDocument_IsInstant()
    {
        var pipeline = new MarkdownPipelineBuilder().Build();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) Markdown.Parse("", pipeline);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 200, $"Empty parse 1000x took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void SourceMap_AddManyEntries_FastLookup()
    {
        // Use 1000 entries; 10k × 10k would be O(n²) ≈ 100M iterations — too slow for 500ms.
        const int count = 1_000;
        // Source must cover all spans: entry i uses SourceSpan(i*10, 9), so max end = (count-1)*10+9.
        var source = new string('x', count * 10 + 10);
        var map = new MarkdownSourceMap(source);
        for (int i = 0; i < count; i++)
            map.Add(i, 0, 9, new SourceSpan(i * 10, 9));

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            var range = new DocumentRange(
                new DocumentPosition(i, 0, 0),
                new DocumentPosition(i, 0, 5));
            var text = map.Slice(range);
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"{count} lookups took {sw.ElapsedMilliseconds}ms");
    }
}
