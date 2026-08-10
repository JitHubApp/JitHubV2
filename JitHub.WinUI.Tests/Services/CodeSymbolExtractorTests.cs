using System;
using System.Diagnostics;
using System.Linq;
using JitHub.Services.CodeViewer;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class CodeSymbolExtractorTests
{
    [Theory]
    [InlineData("csharp", "namespace JitHub;\npublic sealed class Viewer\n{\n public void Open() {}\n}", "Viewer", 2)]
    [InlineData("typescript", "export async function loadProfile() {}", "loadProfile", 1)]
    [InlineData("python", "async def refresh_cache():\n    pass", "refresh_cache", 1)]
    [InlineData("go", "func (s *Store) Reconcile() error { return nil }", "Reconcile", 1)]
    [InlineData("rust", "pub async fn load_tree() {}", "load_tree", 1)]
    public void Extract_FindsLanguageAwareSymbols(
        string language,
        string source,
        string expectedName,
        int expectedLine)
    {
        var symbol = Assert.Single(CodeSymbolExtractor.Extract(language, source), item => item.Name == expectedName);
        Assert.Equal(expectedLine, symbol.LineNumber);
    }

    [Fact]
    public void Extract_UnsupportedLanguageReturnsGracefulEmptyResult()
    {
        Assert.False(CodeSymbolExtractor.Supports("brainfuck"));
        Assert.Empty(CodeSymbolExtractor.Extract("brainfuck", "++++"));
    }

    [Fact]
    public void Extract_LargeFixtureStaysWithinBackgroundIndexBudget()
    {
        string source = string.Join('\n', Enumerable.Range(0, 20_000).Select(index => $"public void Method{index}() {{ }}"));
        Stopwatch stopwatch = Stopwatch.StartNew();

        var symbols = CodeSymbolExtractor.Extract("csharp", source);

        stopwatch.Stop();
        Assert.Equal(2000, symbols.Count);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(150), $"Symbol extraction took {stopwatch.Elapsed.TotalMilliseconds:0.0} ms.");
    }

    [Fact]
    public async System.Threading.Tasks.Task UiWorkBudgetRequestsYieldWithinFiftyMilliseconds()
    {
        UiWorkBudget budget = new(TimeSpan.FromMilliseconds(2));
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!budget.ShouldYield())
        {
            await System.Threading.Tasks.Task.Yield();
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(50));
    }
}
