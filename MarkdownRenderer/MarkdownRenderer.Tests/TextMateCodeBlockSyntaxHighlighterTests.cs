using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.SyntaxHighlighting.TextMate;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class TextMateCodeBlockSyntaxHighlighterTests
{
    [Theory]
    [InlineData("csharp", "public sealed class Demo { }")]
    [InlineData("ts", "const value: string = \"ok\";")]
    [InlineData("python", "def hello():\n    return \"ok\"")]
    [InlineData("powershell", "Write-Host \"ok\"")]
    [InlineData("json", "{ \"ok\": true }")]
    [InlineData("markdown", "# Heading\n\n`code`")]
    [InlineData("css", ".demo { color: red; }")]
    [InlineData("diff", "+added\n-removed")]
    public async Task HighlightAsync_TokenizesRepresentativeLanguages(string language, string code)
    {
        var highlighter = new TextMateCodeBlockSyntaxHighlighter();
        var request = new CodeBlockHighlightRequest(language, code, CodeBlockThemeVariant.Dark, CancellationToken.None);

        var result = await highlighter.HighlightAsync(request);

        Assert.NotNull(result);
        Assert.NotEmpty(result!.Spans);
        Assert.All(result.Spans, span =>
        {
            Assert.InRange(span.Start, 0, code.Length);
            Assert.InRange(span.Length, 1, code.Length);
        });
    }

    [Fact]
    public async Task HighlightAsync_UnknownLanguage_ReturnsEmptyResult()
    {
        var highlighter = new TextMateCodeBlockSyntaxHighlighter();
        var request = new CodeBlockHighlightRequest("definitely-not-real", "hello", CodeBlockThemeVariant.Dark, CancellationToken.None);

        var result = await highlighter.HighlightAsync(request);

        Assert.NotNull(result);
        Assert.Empty(result!.Spans);
    }

    [Fact]
    public async Task HighlightAsync_HighContrastSuppressesTokenColors()
    {
        var highlighter = new TextMateCodeBlockSyntaxHighlighter();
        var request = new CodeBlockHighlightRequest("csharp", "public class Demo { }", CodeBlockThemeVariant.HighContrast, CancellationToken.None);

        var result = await highlighter.HighlightAsync(request);

        Assert.NotNull(result);
        Assert.Empty(result!.Spans);
    }

    [Fact]
    public async Task HighlightAsync_ReusesProviderAcrossLanguages()
    {
        var highlighter = new TextMateCodeBlockSyntaxHighlighter();
        const string csharp = "// comment\npublic sealed class Demo { public string Name => \"ok\"; }";
        var first = await highlighter.HighlightAsync(new CodeBlockHighlightRequest("csharp", csharp, CodeBlockThemeVariant.Dark, CancellationToken.None));

        foreach (var (language, code) in new[]
        {
            ("typescript", "const value: string = \"ok\";"),
            ("python", "def hello():\n    return \"ok\""),
            ("powershell", "Write-Host \"ok\""),
            ("json", "{ \"ok\": true }"),
            ("diff", "+added\n-removed"),
        })
        {
            var mixed = await highlighter.HighlightAsync(new CodeBlockHighlightRequest(language, code, CodeBlockThemeVariant.Dark, CancellationToken.None));
            Assert.NotNull(mixed);
            Assert.NotEmpty(mixed!.Spans);
        }

        var second = await highlighter.HighlightAsync(new CodeBlockHighlightRequest("csharp", csharp, CodeBlockThemeVariant.Dark, CancellationToken.None));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEmpty(first!.Spans);
        Assert.NotEmpty(second!.Spans);
        Assert.True(second.Spans.Select(span => span.Foreground).Distinct().Count() >= 2);
    }

    [Fact]
    public async Task HighlightAsync_HandlesCarriageReturnLineEndings()
    {
        var highlighter = new TextMateCodeBlockSyntaxHighlighter();
        const string code = "// comment\rpublic sealed class Demo\r{\r    public string Name => \"ok\";\r}";

        var result = await highlighter.HighlightAsync(new CodeBlockHighlightRequest("csharp", code, CodeBlockThemeVariant.Dark, CancellationToken.None));

        Assert.NotNull(result);
        Assert.NotEmpty(result!.Spans);
        Assert.True(result.Spans.Select(span => span.Foreground).Distinct().Count() >= 2);
    }

    [Fact]
    public async Task HighlightAsync_AlreadyCanceled_ReturnsNull()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var highlighter = new TextMateCodeBlockSyntaxHighlighter();
        var request = new CodeBlockHighlightRequest("csharp", "public class Demo { }", CodeBlockThemeVariant.Dark, cts.Token);

        var result = await highlighter.HighlightAsync(request);
        Assert.Null(result);
    }
}
