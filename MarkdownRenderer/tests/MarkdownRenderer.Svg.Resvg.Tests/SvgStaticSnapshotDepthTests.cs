using System.Text;
using System.Threading;
using System.Xml;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgStaticSnapshotDepthTests
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private const string HtmlNamespace = "http://www.w3.org/1999/xhtml";
    private const int DeepNesting = 20_000;

    [Fact]
    public void DeepPreloadedXhtmlTextIsFlattenedWithoutRecursiveTraversal()
    {
        var source = new StringBuilder(DeepNesting * 26);
        source.Append(SvgStart);
        source.Append(ForeignObjectStart);
        AppendRepeated(source, "<h:span>", DeepNesting);
        source.Append("deep text");
        AppendRepeated(source, "</h:span>", DeepNesting);
        source.Append(ForeignObjectEnd);
        source.Append(SvgEnd);

        string output = SnapshotPreloadedDocument(source.ToString());

        Assert.Contains(">deep text</text>", output, StringComparison.Ordinal);
        Assert.DoesNotContain("foreignObject", output, StringComparison.Ordinal);
        Assert.DoesNotContain("<h:span", output, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepPreloadedDiscardedSvgGraphicIsValidatedWithoutRecursiveTraversal()
    {
        var source = new StringBuilder(DeepNesting * 8);
        source.Append(SvgStart);
        source.Append(ForeignObjectStart);
        source.Append("<svg:g>");
        AppendRepeated(source, "<svg:g>", DeepNesting);
        source.Append("<svg:path/>");
        AppendRepeated(source, "</svg:g>", DeepNesting + 1);
        source.Append("<h:span>retained label</h:span>");
        source.Append(ForeignObjectEnd);
        source.Append(SvgEnd);

        string output = SnapshotPreloadedDocument(source.ToString());

        Assert.Contains(">retained label</text>", output, StringComparison.Ordinal);
        Assert.DoesNotContain("foreignObject", output, StringComparison.Ordinal);
        Assert.DoesNotContain("<svg:g", output, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepPreloadedStyleInnerTextIsCollectedWithoutRecursiveDomPropertyAccess()
    {
        var source = new StringBuilder(DeepNesting * 8);
        source.Append("<svg xmlns='").Append(SvgNamespace).Append("' width='100' height='100'><style>");
        AppendRepeated(source, "<g>", DeepNesting);
        source.Append("@import 'https://invalid.example/style.css'; .safe { fill: red; }");
        AppendRepeated(source, "</g>", DeepNesting);
        source.Append("</style></svg>");

        string output = SnapshotPreloadedDocument(source.ToString());

        Assert.Contains("<style", output, StringComparison.Ordinal);
        Assert.Contains(".safe", output, StringComparison.Ordinal);
        Assert.DoesNotContain("@import", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<g", output, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextFlatteningPreservesWhitespaceCdataAndNamespacePolicy()
    {
        const string source = "<svg xmlns='" + SvgNamespace + "' xmlns:h='" + HtmlNamespace +
            "' width='100' height='100'><foreignObject x='0' y='0' width='80' height='80'>" +
            "<h:div>one <h:span>two</h:span><![CDATA[   three  ]]><h:br/>four</h:div>" +
            "</foreignObject></svg>";

        string output = Snapshot(source);

        Assert.Contains(">one two three four</text>", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentInsideForeignObjectRemainsUnsupportedAndIsNotFlattened()
    {
        const string source = "<svg xmlns='" + SvgNamespace + "' xmlns:h='" + HtmlNamespace +
            "' width='100' height='100'><foreignObject x='0' y='0' width='80' height='80'>" +
            "<!-- preserved comment --><h:span>label</h:span></foreignObject></svg>";

        string output = Snapshot(source);

        Assert.Contains("<foreignObject", output, StringComparison.Ordinal);
        Assert.Contains("preserved comment", output, StringComparison.Ordinal);
        Assert.DoesNotContain(">label</text>", output, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalEntityExpansionRemainsRejectedByExistingDtdBudget()
    {
        string source = "<!DOCTYPE svg [<!ENTITY payload 'this expansion exceeds the existing entity budget'>]>" +
            "<svg xmlns='" + SvgNamespace + "'><script>&payload;</script></svg>";

        string output = Snapshot(source);

        Assert.Equal(source, output);
    }

    [Fact]
    public void SnapshotStillObservesCancellationBeforeDomWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SvgStaticSnapshot.Create(
            Encoding.UTF8.GetBytes("<svg xmlns='" + SvgNamespace + "'><script/></svg>"),
            cancellation.Token));
    }

    [Fact]
    public void ProductionSnapshotRejectsOverDepthRawXmlBeforeSanitization()
    {
        var source = new StringBuilder(DeepNesting * 8);
        source.Append(SvgStart);
        source.Append("<script/>");
        AppendRepeated(source, "<svg:g>", SvgResourceBudget.MaxDepth + 1);
        AppendRepeated(source, "</svg:g>", SvgResourceBudget.MaxDepth + 1);
        source.Append(SvgEnd);

        SvgStaticSnapshotResourceException exception = Assert.Throws<SvgStaticSnapshotResourceException>(
            () => SvgStaticSnapshot.Create(
                Encoding.UTF8.GetBytes(source.ToString()),
                CancellationToken.None));

        Assert.Equal(SvgStaticSnapshotResourceException.ElementDepthReason, exception.Reason);
    }

    private static string Snapshot(string source) => Encoding.UTF8.GetString(
        SvgStaticSnapshot.Create(Encoding.UTF8.GetBytes(source), CancellationToken.None));

    private static string SnapshotPreloadedDocument(string source)
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        var document = new XmlDocument
        {
            PreserveWhitespace = true,
            XmlResolver = null,
        };
        document.LoadXml(source);

        // These cases test that the post-load traversal stays iterative; they
        // deliberately do not use the production raw-source admission path.
        return Encoding.UTF8.GetString(SvgStaticSnapshot.TransformLoadedDocument(
            sourceBytes,
            document,
            CancellationToken.None));
    }

    private static void AppendRepeated(StringBuilder builder, string value, int count)
    {
        for (int index = 0; index < count; index++)
            builder.Append(value);
    }

    private const string SvgStart = "<svg xmlns='" + SvgNamespace + "' xmlns:h='" + HtmlNamespace +
        "' xmlns:svg='" + SvgNamespace + "' width='100' height='100'>";
    private const string SvgEnd = "</svg>";
    private const string ForeignObjectStart = "<foreignObject x='0' y='0' width='80' height='80'>";
    private const string ForeignObjectEnd = "</foreignObject>";
}
