using System.Text;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgStaticCssPreparationTests
{
    [Fact]
    public void RemovesActiveCssTokensWithoutChangingCommentsQuotesOrSelectors()
    {
        const string css =
            "/* keep @import url('comment.css'); transition: none; */ " +
            ".animation:hover { content: \"@import url('quoted.css'); animation: keep\"; color: red; } " +
            "@import url('remote.css'); " +
            "@font-face { font-family: 'animation: keep'; src: url(data:font/woff;base64,AA==); } " +
            "@keyframes pulse { from { opacity: 0; } to { opacity: 1; } } " +
            ".card { animation-name: pulse; transition-property: opacity; color: blue; }";

        bool changed = SvgStaticSnapshot.TryCreateStaticStyleSheet(
            css,
            CancellationToken.None,
            out string sanitized);

        Assert.True(changed);
        Assert.Equal(css.Length, sanitized.Length);
        Assert.Contains("/* keep @import url('comment.css'); transition: none; */", sanitized, StringComparison.Ordinal);
        Assert.Contains(".animation:hover", sanitized, StringComparison.Ordinal);
        Assert.Contains("content: \"@import url('quoted.css'); animation: keep\"", sanitized, StringComparison.Ordinal);
        Assert.Contains("color: red", sanitized, StringComparison.Ordinal);
        Assert.Contains("color: blue", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("@import url('remote.css')", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("@font-face", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("@keyframes pulse", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("animation-name: pulse", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("transition-property: opacity", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void FullPreparationSanitizesStyleTextAndPreservesQuotedContent()
    {
        byte[] source = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><style><![CDATA[" +
            "/* keep static comment; */ .card { content: 'animation: keep'; color: red; " +
            "transition: opacity 1s; } @import url('remote.css');]]></style></svg>");

        byte[] prepared = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(prepared);

        Assert.Contains("content: 'animation: keep'", text, StringComparison.Ordinal);
        Assert.Contains("color: red", text, StringComparison.Ordinal);
        Assert.Contains("/* keep static comment; */", text, StringComparison.Ordinal);
        Assert.DoesNotContain("@import url('remote.css')", text, StringComparison.Ordinal);
        Assert.DoesNotContain("transition: opacity 1s", text, StringComparison.Ordinal);
        Assert.True(SvgResourceBudget.Validate(prepared, CancellationToken.None).Accepted);
    }

    [Theory]
    [InlineData("/* @import url('comment.css'); */ .card { content: 'transition: keep'; color: red; }")]
    [InlineData(".animation:hover { color: red; }")]
    [InlineData(".card { content: \"@import url('quoted.css');\"; color: red; }")]
    public void IrrelevantTokensReturnOriginalCssWithoutSanitizing(string css)
    {
        bool changed = SvgStaticSnapshot.TryCreateStaticStyleSheet(
            css,
            CancellationToken.None,
            out string sanitized);

        Assert.False(changed);
        Assert.Same(css, sanitized);
    }

    [Theory]
    [InlineData("@import url('theme.css')")]
    [InlineData("@font-face { font-family: 'broken';")]
    [InlineData("animation: 'unterminated")]
    public void MalformedCssKeepsOriginalTextAndIsNotMistakenForSanitization(string css)
    {
        bool changed = SvgStaticSnapshot.TryCreateStaticStyleSheet(
            css,
            CancellationToken.None,
            out string sanitized);

        Assert.False(changed);
        Assert.Same(css, sanitized);
    }

    [Fact]
    public void PreCanceledScanThrowsInsteadOfReturningMalformedCssFallback()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            SvgStaticSnapshot.TryCreateStaticStyleSheet(
                "@import url('missing-semicolon.css')",
                cancellation.Token,
                out _));
    }

    [Fact]
    public void LargeIrrelevantPropertyScanDoesNotAllocatePerPropertyName()
    {
        string css = string.Concat(Enumerable.Repeat(
            ".card { content: 'animation: keep'; color-name: blue; }\n",
            2048));

        for (int index = 0; index < 2; index++)
        {
            Assert.False(SvgStaticSnapshot.TryCreateStaticStyleSheet(
                css,
                CancellationToken.None,
                out string warmup));
            Assert.Same(css, warmup);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool changed = SvgStaticSnapshot.TryCreateStaticStyleSheet(
            css,
            CancellationToken.None,
            out string sanitized);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(changed);
        Assert.Same(css, sanitized);
        Assert.True(allocated < 8192, $"Irrelevant CSS scan allocated {allocated} bytes.");
    }

    [Fact]
    public void FirstActiveDeclarationAfterLargeInertPrefixIsRemovedExactly()
    {
        string prefix = string.Concat(Enumerable.Repeat(
            ".card { color-name: blue; }\n",
            2048));
        const string activeRule = ".card { animation: pulse; color: red; }";
        string css = prefix + activeRule;

        bool changed = SvgStaticSnapshot.TryCreateStaticStyleSheet(
            css,
            CancellationToken.None,
            out string sanitized);

        string expectedActiveRule = ".card { " +
            new string(' ', "animation: pulse;".Length) +
            " color: red; }";
        Assert.True(changed);
        Assert.Equal(css.Length, sanitized.Length);
        Assert.StartsWith(prefix, sanitized, StringComparison.Ordinal);
        Assert.EndsWith(expectedActiveRule, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("animation: pulse", sanitized, StringComparison.Ordinal);
    }
}
