using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Xml;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgStaticSnapshotWideInputTests
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private const string HtmlNamespace = "http://www.w3.org/1999/xhtml";

    [Theory]
    [InlineData("0 0 120 45", true, true, 120)]
    [InlineData("0,0,120,45", false, true, 45)]
    [InlineData("  0,\u00a00  , 120   45  ", true, true, 120)]
    [InlineData("0,,0,120,45,60", true, false, 0)]
    [InlineData("0 0 120 45 60", true, false, 0)]
    [InlineData("0\t0 120 45", true, false, 0)]
    [InlineData("0 0 NaN 45", true, false, 0)]
    [InlineData("0 0 120 Infinity", false, false, 0)]
    [InlineData("0 0 -120 45", true, false, 0)]
    public void ViewBoxSpanParserMatchesSplitAndTrimSemantics(
        string source,
        bool horizontal,
        bool expectedSuccess,
        double expectedExtent)
    {
        bool oldSuccess = ParseViewBoxUsingOriginalSplit(source, horizontal, out double oldExtent);
        bool success = SvgStaticSnapshot.TryParseViewBoxExtent(
            source,
            horizontal,
            CancellationToken.None,
            out double extent);

        Assert.Equal(oldSuccess, success);
        Assert.Equal(expectedSuccess, success);
        if (double.IsNaN(oldExtent))
            Assert.True(double.IsNaN(extent));
        else
            Assert.Equal(oldExtent, extent);
        if (success)
            Assert.Equal(expectedExtent, extent);
    }

    [Fact]
    public void ViewBoxSpanParserDoesNotAllocatePerToken()
    {
        const string source = "\u00a00, 0\t 100,\u2003200 ";
        bool succeeded = false;
        for (int index = 0; index < 32; index++)
        {
            succeeded |= SvgStaticSnapshot.TryParseViewBoxExtent(
                source,
                horizontal: true,
                CancellationToken.None,
                out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 256; index++)
        {
            succeeded &= SvgStaticSnapshot.TryParseViewBoxExtent(
                source,
                horizontal: true,
                CancellationToken.None,
                out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(succeeded);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void ViewBoxParserObservesCancellationBeforeScanning()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SvgStaticSnapshot.TryParseViewBoxExtent(
            new string(' ', 32_768),
            horizontal: true,
            cancellation.Token,
            out _));
    }

    [Fact]
    public void ForeignObjectPercentageExtentUsesAllocationFreeViewBoxScanner()
    {
        byte[] source = Encoding.UTF8.GetBytes(
            "<svg xmlns='" + SvgNamespace + "' viewBox='0, 0 100   50'>" +
            "<foreignObject x='0' y='0' width='50%' height='50%' style='text-align:center;font-size:30px'>" +
            "<div xmlns='" + HtmlNamespace + "'>label</div>" +
            "</foreignObject></svg>");

        XmlDocument document = Load(SvgStaticSnapshot.Create(source, CancellationToken.None));

        XmlElement text = Assert.IsType<XmlElement>(document.GetElementsByTagName("text", SvgNamespace)[0]);
        Assert.Equal("25", text.GetAttribute("x"));
        Assert.Equal("25", text.GetAttribute("y"));
        Assert.Equal("label", text.InnerText);
    }

    [Fact]
    public void ForeignObjectStyleParserPreservesLastWinsAndMalformedDeclarationSemantics()
    {
        const string source =
            " font-size : 999999999999999999999999999999999px ; FONT-SIZE: 14px ;" +
            "color: red : authored; malformed; letter-spacing: -2.5px; color: ; ;";
        Dictionary<string, string> expected = ParseStyleUsingOriginalSplit(source);
        Dictionary<string, string> actual = SvgStaticSnapshot.ParseStyle(source, CancellationToken.None);

        Assert.Equal(expected.Count, actual.Count);
        foreach (KeyValuePair<string, string> pair in expected)
        {
            Assert.True(actual.TryGetValue(pair.Key, out string? value));
            Assert.Equal(pair.Value, value);
        }
        Assert.Equal("14px", actual["font-size"]);
        Assert.Equal("red : authored", actual["COLOR"]);
    }

    [Fact]
    public void RepeatedUnknownAndOverwrittenStyleValuesDoNotCreatePerDeclarationStrings()
    {
        string repeatedUnknown = RepeatDeclaration("unrecognized:value;", 20_000);
        string largeValue = new string('x', 256_000);
        string overwritten = "font-size:" + largeValue + ";FONT-SIZE:16px";
        string rejectedWithLargeValue = "font-size:" + largeValue + ";custom:value";

        _ = SvgStaticSnapshot.ParseStyle(repeatedUnknown, CancellationToken.None);
        _ = SvgStaticSnapshot.ParseStyle(overwritten, CancellationToken.None);
        _ = SvgStaticSnapshot.ParseStyle(rejectedWithLargeValue, CancellationToken.None);
        // The thread counter can pick up a one-off runtime allocation, such as
        // an event listener from a parallel test class. A per-declaration
        // string costs hundreds of KB on every attempt, so it still fails.
        Dictionary<string, string> unknown = [];
        Dictionary<string, string> finalValue = [];
        Dictionary<string, string> rejected = [];
        long unknownAllocated = 0, overwriteAllocated = 0, rejectedAllocated = 0;
        var attempts = new List<string>(capacity: 5);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            long beforeUnknown = GC.GetAllocatedBytesForCurrentThread();
            unknown = SvgStaticSnapshot.ParseStyle(repeatedUnknown, CancellationToken.None);
            unknownAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeUnknown;
            long beforeOverwrite = GC.GetAllocatedBytesForCurrentThread();
            finalValue = SvgStaticSnapshot.ParseStyle(overwritten, CancellationToken.None);
            overwriteAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeOverwrite;
            long beforeRejected = GC.GetAllocatedBytesForCurrentThread();
            rejected = SvgStaticSnapshot.ParseStyle(rejectedWithLargeValue, CancellationToken.None);
            rejectedAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeRejected;
            attempts.Add($"{unknownAllocated}/{overwriteAllocated}/{rejectedAllocated}");
            if (unknownAllocated < 4_096 && overwriteAllocated < 4_096 && rejectedAllocated < 4_096)
                break;
        }

        Assert.Single(unknown);
        Assert.Equal("16px", finalValue["font-size"]);
        Assert.Single(rejected);
        string history = $" Attempts (unknown/overwritten/rejected bytes): {string.Join(", ", attempts)}.";
        Assert.True(unknownAllocated < 4_096, $"Repeated unknown declarations allocated {unknownAllocated} bytes." + history);
        Assert.True(overwriteAllocated < 4_096, $"Overwritten value declarations allocated {overwriteAllocated} bytes." + history);
        Assert.True(rejectedAllocated < 4_096, $"Rejected declarations allocated {rejectedAllocated} bytes." + history);
    }

    [Fact]
    public void ForeignObjectStyleParserObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SvgStaticSnapshot.ParseStyle(
            "font-size:16px",
            cancellation.Token));
    }

    [Fact]
    public void PlainTextReplacementStillUsesTheLastCaseInsensitiveStyleValue()
    {
        string oversizedFirstValue = new string('9', 256_000);
        byte[] source = Encoding.UTF8.GetBytes(
            "<svg xmlns='" + SvgNamespace + "'>" +
            "<foreignObject x='0' y='0' width='100' height='24' style='font-size:" +
            oversizedFirstValue + "px;FONT-SIZE:16px'>" +
            "<div xmlns='" + HtmlNamespace + "'>label</div>" +
            "</foreignObject></svg>");

        XmlDocument document = Load(SvgStaticSnapshot.Create(source, CancellationToken.None));

        XmlElement text = Assert.IsType<XmlElement>(document.GetElementsByTagName("text", SvgNamespace)[0]);
        Assert.Equal("16", text.GetAttribute("font-size"));
        Assert.Equal("label", text.InnerText);
    }

    [Fact]
    public void PlainTextReplacementStillRejectsUnsupportedStylesAfterDuplicateOverwrite()
    {
        byte[] source = Encoding.UTF8.GetBytes(
            "<svg xmlns='" + SvgNamespace + "'>" +
            "<foreignObject x='0' y='0' width='100' height='24' style='custom:value;CUSTOM:other'>" +
            "<div xmlns='" + HtmlNamespace + "'>label</div>" +
            "</foreignObject></svg>");

        byte[] result = SvgStaticSnapshot.Create(source, CancellationToken.None);

        Assert.Same(source, result);
    }

    [Theory]
    [InlineData("width:100%;font-size:12px", true)]
    [InlineData("width:100%;;color:red", true)]
    [InlineData("width:100%:authored", true)]
    [InlineData("Width:100%", false)]
    [InlineData("width:100%;WIDTH:90%", false)]
    [InlineData("width:100%; ;color:red", false)]
    [InlineData("width:100%;color", false)]
    [InlineData("width:100%;color:", false)]
    public void DiscardedHtmlStyleValidationPreservesExactCaseAndSplitBehavior(
        string source,
        bool expected)
    {
        Assert.Equal(expected, SvgStaticSnapshot.IsSafeDiscardedHtmlTextStyle(
            source.AsSpan(),
            CancellationToken.None));
    }

    [Fact]
    public void DiscardedHtmlStyleValidationObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SvgStaticSnapshot.IsSafeDiscardedHtmlTextStyle(
            "width:100%".AsSpan(),
            cancellation.Token));
    }

    [Fact]
    public void PaddedHtmlAttributesKeepTrimmedAcceptanceAndStrictStyleRejection()
    {
        XmlDocument accepted = Load(
            "<div xmlns='" + HtmlNamespace + "' class=' safe ' id=' id-1 ' align=' center '" +
            " colspan=' 2 ' style=' width:100%; color:red '/>");
        XmlDocument rejected = Load(
            "<div xmlns='" + HtmlNamespace + "' style=' width:100%;  ; color:red '/>");

        Assert.True(SvgStaticSnapshot.HasOnlySafeHtmlTextAttributes(
            accepted.DocumentElement!,
            CancellationToken.None));
        Assert.False(SvgStaticSnapshot.HasOnlySafeHtmlTextAttributes(
            rejected.DocumentElement!,
            CancellationToken.None));
    }

    [Fact]
    public void PaddedHtmlAttributeTrimObservesCancellation()
    {
        XmlDocument document = Load(
            "<div xmlns='" + HtmlNamespace + "' class='       safe       '/>");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SvgStaticSnapshot.HasOnlySafeHtmlTextAttributes(
            document.DocumentElement!,
            cancellation.Token));
    }

    [Fact]
    public void SwitchFallbackMemoTracksTextCreatedByAnEarlierForeignObject()
    {
        byte[] source = Encoding.UTF8.GetBytes(
            "<svg xmlns='" + SvgNamespace + "'><switch>" +
            ForeignObject("first") + ForeignObject("second") + ForeignObject("third") +
            "</switch></svg>");

        byte[] result = SvgStaticSnapshot.Create(source, CancellationToken.None);
        XmlDocument document = Load(result);

        Assert.Equal(1, document.GetElementsByTagName("text", SvgNamespace).Count);
        Assert.Equal(0, document.GetElementsByTagName("foreignObject", SvgNamespace).Count);
        Assert.Equal("first", document.GetElementsByTagName("text", SvgNamespace)[0]!.InnerText);
    }

    [Fact]
    public void ScriptAndAnimationRemovedBeforeSwitchFallbackCheckDoNotCountAsFallbacks()
    {
        byte[] source = Encoding.UTF8.GetBytes(
            "<svg xmlns='" + SvgNamespace + "'><switch>" +
            "<script/><animate attributeName='opacity' values='0;1'/>" +
            ForeignObject("first") + ForeignObject("second") +
            "</switch></svg>");

        XmlDocument document = Load(SvgStaticSnapshot.Create(source, CancellationToken.None));

        Assert.Equal(1, document.GetElementsByTagName("text", SvgNamespace).Count);
        Assert.Equal(0, document.GetElementsByTagName("script", SvgNamespace).Count);
        Assert.Equal(0, document.GetElementsByTagName("animate", SvgNamespace).Count);
    }

    [Fact]
    public void SwitchFallbackSearchChecksCancellationWhileWalkingWideSiblingList()
    {
        XmlDocument document = Load(
            "<svg xmlns='" + SvgNamespace + "'><switch>" +
            RepeatForeignObject(256) +
            "</switch></svg>");
        XmlElement target = (XmlElement)document.GetElementsByTagName("foreignObject", SvgNamespace)[0]!;
        Dictionary<XmlElement, bool>? memo = null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SvgStaticSnapshot.HasSwitchFallback(
            target,
            ref memo,
            cancellation.Token));
    }

    [Fact]
    public void WideSwitchMemoUsesOneParentEntryWithoutPerForeignObjectAllocations()
    {
        XmlDocument document = Load(
            "<svg xmlns='" + SvgNamespace + "'><switch>" +
            RepeatForeignObject(256) +
            "</switch></svg>");
        XmlNodeList nodes = document.GetElementsByTagName("foreignObject", SvgNamespace);
        var foreignObjects = new XmlElement[nodes.Count];
        for (int index = 0; index < foreignObjects.Length; index++)
            foreignObjects[index] = (XmlElement)nodes[index]!;

        Dictionary<XmlElement, bool>? memo = null;
        for (int index = 0; index < 4; index++)
            Assert.False(SvgStaticSnapshot.HasSwitchFallback(foreignObjects[index], ref memo, CancellationToken.None));

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allHaveNoFallback = true;
        for (int index = 0; index < foreignObjects.Length; index++)
        {
            allHaveNoFallback &= !SvgStaticSnapshot.HasSwitchFallback(
                foreignObjects[index],
                ref memo,
                CancellationToken.None);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allHaveNoFallback);
        Assert.Single(memo!);
        Assert.True(allocated < 4_096, $"Wide sibling memoization allocated {allocated} bytes.");
    }

    private static bool ParseViewBoxUsingOriginalSplit(string source, bool horizontal, out double extent)
    {
        extent = double.NaN;
        string[] tokens = source.Split(
            [' ', ','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length == 4 &&
            double.TryParse(tokens[horizontal ? 2 : 3], NumberStyles.Float,
                CultureInfo.InvariantCulture, out extent) &&
            double.IsFinite(extent) &&
            extent > 0;
    }

    private static Dictionary<string, string> ParseStyleUsingOriginalSplit(string source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string declaration in source.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = declaration.IndexOf(':');
            if (separator <= 0)
                continue;
            string name = declaration[..separator].Trim();
            string propertyValue = declaration[(separator + 1)..].Trim();
            if (name.Length > 0 && propertyValue.Length > 0)
                result[name] = propertyValue;
        }
        return result;
    }

    private static string RepeatDeclaration(string declaration, int count)
    {
        var builder = new StringBuilder(declaration.Length * count);
        for (int index = 0; index < count; index++)
            builder.Append(declaration);
        return builder.ToString();
    }

    private static string RepeatForeignObject(int count)
    {
        var builder = new StringBuilder(count * 128);
        for (int index = 0; index < count; index++)
            builder.Append(ForeignObject("fallback"));
        return builder.ToString();
    }

    private static string ForeignObject(string text) =>
        "<foreignObject x='0' y='0' width='100' height='24'><div xmlns='" +
        HtmlNamespace + "'>" + text + "</div></foreignObject>";

    private static XmlDocument Load(byte[] bytes)
    {
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        using var stream = new System.IO.MemoryStream(bytes, writable: false);
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { XmlResolver = null });
        document.Load(reader);
        return document;
    }

    private static XmlDocument Load(string xml) => Load(Encoding.UTF8.GetBytes(xml));
}
