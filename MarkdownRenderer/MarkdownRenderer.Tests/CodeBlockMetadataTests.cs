using Markdig;
using Markdig.Syntax;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class CodeBlockMetadataTests
{
    [Fact]
    public void CopyPayload_UsesDisplayedCodeTextWithoutFences()
    {
        var document = Markdown.Parse("```js\nconsole.log(1);\nconsole.log(2);\n```\n");
        var block = Assert.IsType<FencedCodeBlock>(document[0]);

        string payload = CodeBlockMetadata.CopyPayload(block.Lines.ToString());

        Assert.Equal("console.log(1);\nconsole.log(2);", payload);
        Assert.DoesNotContain("```", payload);
        Assert.DoesNotContain("js", payload);
    }

    [Fact]
    public void CopyPayload_NormalizesCarriageReturnLineEndings()
    {
        string payload = CodeBlockMetadata.CopyPayload("alpha\rbeta\r\ngamma");

        Assert.Equal("alpha\nbeta\ngamma", payload);
    }

    [Theory]
    [InlineData(null, "Code")]
    [InlineData("", "Code")]
    [InlineData("csharp", "C#")]
    [InlineData("cs", "C#")]
    [InlineData("js", "JavaScript")]
    [InlineData("javascript", "JavaScript")]
    [InlineData("ts", "TypeScript")]
    [InlineData("typescript", "TypeScript")]
    [InlineData("python", "Python")]
    [InlineData("py", "Python")]
    [InlineData("powershell", "PowerShell")]
    [InlineData("pwsh", "PowerShell")]
    [InlineData("custom-lang", "custom-lang")]
    public void DisplayLanguage_NormalizesAliases_AndPreservesUnknown(string? input, string expected)
    {
        Assert.Equal(expected, CodeBlockMetadata.DisplayLanguage(input));
    }

    [Fact]
    public void FromBlock_ParsesV2FenceMetadata()
    {
        var document = Markdown.Parse("```csharp filename=\"src/App.cs\" title=\"Main app\" {1,3-5} showLineNumbers startLine=10 diff ignored\n+Console.WriteLine(1);\n```\n");
        var block = Assert.IsType<FencedCodeBlock>(document[0]);

        var metadata = CodeBlockMetadata.FromBlock(block, block.Lines.ToString());

        Assert.Equal("csharp", metadata.Language);
        Assert.Equal("C#", metadata.LanguageDisplay);
        Assert.Equal("src/App.cs", metadata.FileName);
        Assert.Equal("Main app", metadata.Title);
        Assert.True(metadata.ShowLineNumbers);
        Assert.Equal(10, metadata.StartLine);
        Assert.True(metadata.IsDiff);
        Assert.True(metadata.HighlightedLines.Contains(1));
        Assert.False(metadata.HighlightedLines.Contains(2));
        Assert.True(metadata.HighlightedLines.Contains(3));
        Assert.True(metadata.HighlightedLines.Contains(5));
        Assert.False(metadata.HighlightedLines.Contains(6));
    }

    [Fact]
    public void FromBlock_NoLineNumbers_OverridesShowLineNumbers()
    {
        var document = Markdown.Parse("```ts showLineNumbers noLineNumbers\nconst x = 1;\n```\n");
        var block = Assert.IsType<FencedCodeBlock>(document[0]);

        var metadata = CodeBlockMetadata.FromBlock(block, block.Lines.ToString());

        Assert.Equal("typescript", metadata.Language);
        Assert.False(metadata.ShowLineNumbers);
    }
}
