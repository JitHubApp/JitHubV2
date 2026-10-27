using Xunit;

namespace MarkdownRenderer.Math.Tests;

public sealed class NativeParserRecursionSafetyTests
{
    [Fact]
    public async Task UnbracedCommandArgumentRecursion_AtConfiguredDepthDoesNotTripLimit()
    {
        const int configuredDepth = 4;
        // A digit terminates the final control word while remaining a valid
        // unbraced argument. A trailing letter would turn the last token into
        // the unknown command "\sqrtx" and would not exercise this boundary.
        string tex = string.Concat(Enumerable.Repeat(@"\sqrt", configuredDepth)) + "1";
        string markdown = "$" + tex + "$";
        var request = new MathFormulaRequest(
            markdown,
            tex,
            new MathSourceRange(0, markdown.Length),
            new MathSourceRange(1, tex.Length),
            MathFormulaDisplayMode.Inline);

        MathFormulaResult result = await new MathFormulaProcessor().ProcessAsync(
            request,
            new MathProcessingOptions(maximumNestingDepth: configuredDepth));

        Assert.Equal(MathFormulaResultKind.Accepted, result.Kind);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public async Task UnbracedCommandArgumentRecursion_ReturnsNestingLimitWithExactFallback()
    {
        const int configuredDepth = 4;
        // Exercise the first disallowed parser frame so a future off-by-one
        // regression cannot hide behind a source that exceeds the budget by two.
        string tex = string.Concat(Enumerable.Repeat(@"\sqrt", configuredDepth + 1)) + "1";
        string markdown = "$" + tex + "$";
        var request = new MathFormulaRequest(
            markdown,
            tex,
            new MathSourceRange(0, markdown.Length),
            new MathSourceRange(1, tex.Length),
            MathFormulaDisplayMode.Inline);

        MathFormulaResult result = await new MathFormulaProcessor().ProcessAsync(
            request,
            new MathProcessingOptions(maximumNestingDepth: configuredDepth));

        Assert.Equal(MathFormulaResultKind.Unsupported, result.Kind);
        Assert.Equal("MATH102", result.Diagnostic?.Code);
        Assert.Equal(request.ContentRange, result.Diagnostic?.SourceRange);
        Assert.Equal(markdown, result.FallbackSource);
        Assert.Null(result.Scene);
    }

    [Fact]
    public async Task RelaxedConfiguredDepth_RemainsBoundedByHardParserCeiling()
    {
        const int commandCount = 512;
        string tex = string.Concat(Enumerable.Repeat(@"\sqrt", commandCount)) + "1";
        string markdown = "$" + tex + "$";
        var request = new MathFormulaRequest(
            markdown,
            tex,
            new MathSourceRange(0, markdown.Length),
            new MathSourceRange(1, tex.Length),
            MathFormulaDisplayMode.Inline);

        MathFormulaResult result = await new MathFormulaProcessor().ProcessAsync(
            request,
            new MathProcessingOptions(maximumNestingDepth: int.MaxValue));

        Assert.Equal(MathFormulaResultKind.Unsupported, result.Kind);
        Assert.Equal("MATH102", result.Diagnostic?.Code);
        Assert.Equal(markdown, result.FallbackSource);
    }
}
