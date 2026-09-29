using System.Text;
using MarkdownRenderer.Parsing;
using Xunit;

namespace MarkdownRenderer.Html.Tests;

public sealed class SafeHtmlFuzzTests
{
    [Fact]
    public void ArbitraryHtml_StaysWithinBudgetsAndNeverProducesInvalidSourceRanges()
    {
        var random = new Random(0x48_54_4D_4C);
        var limits = new SafeHtmlParseLimits(
            MaxInputLength: 4096,
            MaxNodeCount: 256,
            MaxNestingDepth: 16,
            MaxAttributeCount: 8,
            MaxAttributeValueLength: 128,
            MaxTagLength: 512);

        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            string input = CreateArbitraryHtml(random, random.Next(0, 8193));
            string boundedInput = input.Length > limits.MaxInputLength
                ? input[..limits.MaxInputLength]
                : input;

            SafeHtmlDocument document = SafeHtmlParser.Parse(input, limits);

            int nodeCount = 0;
            ValidateNode(document.Root, boundedInput.Length, 0, limits, ref nodeCount);
            Assert.InRange(nodeCount, 1, limits.MaxNodeCount + 1);
            if (input.Length > limits.MaxInputLength)
                Assert.True(document.IsTruncated);
        }
    }

    private static void ValidateNode(
        SafeHtmlNode node,
        int sourceLength,
        int depth,
        SafeHtmlParseLimits limits,
        ref int nodeCount)
    {
        nodeCount++;
        Assert.InRange(node.SourceStart, 0, sourceLength);
        Assert.InRange(node.SourceLength, 0, sourceLength - node.SourceStart);
        Assert.InRange(depth, 0, limits.MaxNestingDepth + 1);

        if (node is not SafeHtmlElement element)
            return;

        Assert.InRange(element.Attributes.Count, 0, limits.MaxAttributeCount);
        Assert.All(element.Attributes, pair =>
            Assert.InRange(pair.Value.Length, 0, limits.MaxAttributeValueLength));
        foreach (SafeHtmlNode child in element.Children)
            ValidateNode(child, sourceLength, depth + 1, limits, ref nodeCount);
    }

    private static string CreateArbitraryHtml(Random random, int targetLength)
    {
        string[] tokens =
        [
            "<", ">", "</", "/>", "<!--", "-->", "<!doctype html>", "=", "'", "\"",
            "script", "style", "iframe", "object", "img", "a", "details", "summary", "table",
            "div", "span", "br", "data:", "javascript:", "file:", "https://", "onerror", "onclick",
            "class", "style", "width", "height", "&amp;", "&#0;", "&#x1F9EA;", "\0", "\r\n",
            "\uD800", "\uDC00", "😀", "text", " \t",
        ];
        var builder = new StringBuilder(targetLength + 16);
        while (builder.Length < targetLength)
        {
            string token = tokens[random.Next(tokens.Length)];
            int remaining = targetLength - builder.Length;
            builder.Append(token.AsSpan(0, Math.Min(token.Length, remaining)));
        }

        return builder.ToString();
    }
}
