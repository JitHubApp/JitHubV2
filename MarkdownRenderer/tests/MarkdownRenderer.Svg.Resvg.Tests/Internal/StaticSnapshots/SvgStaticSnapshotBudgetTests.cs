using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Svg.Resvg.Internal.Preparation;
using MarkdownRenderer.Svg.Resvg.Internal.StaticSnapshots;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests.Internal.StaticSnapshots;

public sealed class SvgStaticSnapshotBudgetTests
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    [Fact]
    public void StaticUtf8SourceUsesNoSerializationAndPreservesInputArray()
    {
        byte[] source = Encoding.UTF8.GetBytes(
            $"<svg xmlns='{SvgNamespace}'><rect width='1' height='1'/></svg>");

        byte[] prepared = SvgStaticSnapshot.Create(source, CancellationToken.None);

        Assert.Same(source, prepared);
    }

    [Fact]
    public void MarkerScannerMatchesAsciiCaseAcrossPeriodicScanBoundary()
    {
        string[] markers =
        [
            "<ANIMATE", "<SCRIPT", "<SET", "<DISCARD", "<FOREIGNOBJECT",
            "@FONT-FACE", "@IMPORT", "@KEYFRAMES", "@-WEBKIT-KEYFRAMES",
            "ANIMATION", "TRANSITION",
        ];

        foreach (string marker in markers)
        {
            byte[] source = Encoding.ASCII.GetBytes(new string('x', 4095) + marker);
            Assert.True(
                SvgStaticSnapshotPrescan.MayRequireStaticSnapshot(source, CancellationToken.None),
                $"Expected marker '{marker}' to be found across the scan checkpoint.");
        }

        Assert.False(SvgStaticSnapshotPrescan.MayRequireStaticSnapshot(
            Encoding.ASCII.GetBytes(new string('x', 8192)),
            CancellationToken.None));
    }

    [Fact]
    public void MarkerScanHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SvgStaticSnapshotPrescan.MayRequireStaticSnapshot(new byte[8192], cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            SvgStaticSnapshotPrescan.MayRequireStaticSnapshot(ReadOnlySpan<byte>.Empty, cancellation.Token));
    }

    [Fact]
    public void MarkerInCommentCanTriggerInspectionWithoutChangingSourceBytes()
    {
        byte[] source = Encoding.UTF8.GetBytes(
            $"<svg xmlns='{SvgNamespace}'><!-- animation is inert text --><rect/></svg>");

        byte[] prepared = SvgStaticSnapshot.Create(source, CancellationToken.None);

        Assert.Same(source, prepared);
    }

    [Fact]
    public void ChangedLegacyEncodingIsSerializedAsValidUtf8AndKeepsStaticAnimationValue()
    {
        const string legacySvg = "<?xml version='1.0' encoding='iso-8859-1'?>" +
            "<svg xmlns='http://www.w3.org/2000/svg'><rect aria-label='caf\u00e9'/>" +
            "<animate attributeName='opacity' from='0.5'/></svg>";
        byte[] source = Encoding.Latin1.GetBytes(legacySvg);

        byte[] prepared = SvgStaticSnapshot.Create(source, CancellationToken.None);

        string serialized = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(prepared);
        Assert.Contains("caf\u00e9", serialized, StringComparison.Ordinal);
        Assert.Contains("opacity=\"0.5\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("<animate", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransformedOutputFitsExactCeilingAndRejectsOneByteLess()
    {
        byte[] source = Encoding.UTF8.GetBytes(
            $"<svg xmlns='{SvgNamespace}'><rect/><animate attributeName='opacity' from='0.5'/></svg>");
        byte[] expected = SvgStaticSnapshot.Create(source, CancellationToken.None);

        byte[] exact = SvgStaticSnapshot.Create(source, CancellationToken.None, expected.Length);

        Assert.Equal(expected, exact);
        SvgStaticSnapshotResourceException exception = Assert.Throws<SvgStaticSnapshotResourceException>(
            () => SvgStaticSnapshot.Create(source, CancellationToken.None, expected.Length - 1));
        Assert.Equal(SvgStaticSnapshotResourceException.OutputBytesReason, exception.Reason);
    }

    [Fact]
    public void LoweredOutputCeilingAllowsLargerRawSourceWhenSanitizationShrinksIt()
    {
        string scriptPayload = new('x', 512);
        byte[] source = Encoding.UTF8.GetBytes(
            $"<svg xmlns='{SvgNamespace}'><script>{scriptPayload}</script><rect/></svg>");
        byte[] expected = SvgStaticSnapshot.Create(source, CancellationToken.None);

        byte[] prepared = SvgStaticSnapshot.Create(source, CancellationToken.None, expected.Length);

        Assert.Equal(expected, prepared);
        Assert.DoesNotContain("script", Encoding.UTF8.GetString(prepared), StringComparison.OrdinalIgnoreCase);
        Assert.True(source.Length > prepared.Length);
    }

    [Fact]
    public void CancellationDuringXmlReaderInputIsPropagatedAndDoesNotBecomeInvalidXmlFallback()
    {
        byte[] source = Encoding.UTF8.GetBytes(
            $"<svg xmlns='{SvgNamespace}'><desc>{new string('x', 32_768)}</desc></svg>");
        using var cancellation = new CancellationTokenSource();
        using var input = new CancelAfterFirstReadStream(source, cancellation);

        Assert.ThrowsAny<OperationCanceledException>(
            () => SvgStaticSnapshot.LoadDocument(input, cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public void BudgetedReaderStopsWhenCancellationArrivesDuringACompletedParserRead()
    {
        using var cancellation = new CancellationTokenSource();
        var parser = new CancelAfterReadXmlReader(cancellation);
        using var reader = new BudgetedXmlReader(parser, cancellation.Token);

        Assert.ThrowsAny<OperationCanceledException>(() => reader.Read());
        Assert.Equal(1, parser.ReadCalls);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public void StructuralReaderAllowsInclusiveDepthLimitAndRejectsTheNextLevel()
    {
        byte[] atLimit = CreateNestedSvg(SvgResourceBudget.MaxDepth);
        using (var input = new MemoryStream(atLimit, writable: false))
        {
            XmlDocument document = SvgStaticSnapshot.LoadDocument(input, CancellationToken.None);
            Assert.NotNull(document.DocumentElement);
        }

        byte[] overLimit = CreateNestedSvg(SvgResourceBudget.MaxDepth + 1);
        using var overLimitInput = new MemoryStream(overLimit, writable: false);
        SvgStaticSnapshotResourceException exception = Assert.Throws<SvgStaticSnapshotResourceException>(
            () => SvgStaticSnapshot.LoadDocument(overLimitInput, CancellationToken.None));

        Assert.Equal(SvgStaticSnapshotResourceException.ElementDepthReason, exception.Reason);
    }

    [Fact]
    public void StructuralReaderAllowsInclusiveElementLimitAndRejectsTheNextElement()
    {
        byte[] atLimit = CreateWideSvg(SvgResourceBudget.MaxElements - 1);
        using (var input = new MemoryStream(atLimit, writable: false))
        {
            XmlDocument document = SvgStaticSnapshot.LoadDocument(input, CancellationToken.None);
            Assert.NotNull(document.DocumentElement);
        }

        byte[] overLimit = CreateWideSvg(SvgResourceBudget.MaxElements);
        using var overLimitInput = new MemoryStream(overLimit, writable: false);
        SvgStaticSnapshotResourceException exception = Assert.Throws<SvgStaticSnapshotResourceException>(
            () => SvgStaticSnapshot.LoadDocument(overLimitInput, CancellationToken.None));

        Assert.Equal(SvgStaticSnapshotResourceException.ElementCountReason, exception.Reason);
    }

    [Fact]
    public void StructuralReaderCountsNamespaceAndOrdinaryAttributesCumulatively()
    {
        using XmlReader parser = CreateParser(
            "<svg xmlns='urn:svg' xmlns:p='urn:prefix'><p:g value='1'/></svg>");
        var budget = new XmlStructuralBudget(
            attributesSeen: SvgResourceBudget.MaxAttributes - 2);
        using var reader = new BudgetedXmlReader(
            parser,
            CancellationToken.None,
            budget);

        Assert.True(reader.Read());
        Assert.Equal(XmlNodeType.Element, reader.NodeType);
        Assert.Equal("urn:svg", reader.NamespaceURI);
        Assert.Equal(2, reader.AttributeCount);

        SvgStaticSnapshotResourceException exception = Assert.Throws<SvgStaticSnapshotResourceException>(
            () => reader.Read());
        Assert.Equal(SvgStaticSnapshotResourceException.AttributeCountReason, exception.Reason);
    }

    [Fact]
    public void BudgetedLoadPreservesNamespaceXmlNodesAndStaticSanitizationBehavior()
    {
        const string sourceText =
            "<?xml version='1.0'?>" +
            "<!DOCTYPE svg SYSTEM 'https://example.invalid/svg.dtd'>" +
            "<svg xmlns='http://www.w3.org/2000/svg' xmlns:p='urn:preserved'>" +
            "\n<!--keep-comment--><?keep value?><p:node><![CDATA[keep-cdata]]></p:node>" +
            "<script>never-execute</script><animate attributeName='opacity' from='0.5'/>" +
            "</svg>";
        byte[] source = Encoding.UTF8.GetBytes(sourceText);

        XmlDocument parsed;
        using (var input = new MemoryStream(source, writable: false))
            parsed = SvgStaticSnapshot.LoadDocument(input, CancellationToken.None);

        Assert.True(parsed.PreserveWhitespace);
        Assert.Equal("svg", parsed.DocumentType?.Name);
        // XmlDocument represents an absent internal subset as an empty string
        // with this parser. Compare the unchanged parser path as well as its
        // inert value, rather than assuming a nullable representation.
        var baseline = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        using (XmlReader parser = CreateParser(sourceText))
            baseline.Load(parser);
        Assert.Equal(baseline.OuterXml, parsed.OuterXml);
        Assert.Equal(string.Empty, parsed.DocumentType?.InternalSubset);
        Assert.Equal("http://www.w3.org/2000/svg", parsed.DocumentElement?.NamespaceURI);
        Assert.Contains(parsed.SelectNodes("//comment()")!.OfType<XmlComment>(),
            static comment => comment.Value == "keep-comment");
        Assert.Contains(parsed.SelectNodes("//processing-instruction('keep')")!.OfType<XmlProcessingInstruction>(),
            static instruction => instruction.Data == "value");
        XmlElement preserved = Assert.IsType<XmlElement>(parsed.GetElementsByTagName("node", "urn:preserved")[0]);
        Assert.IsType<XmlCDataSection>(preserved.FirstChild);

        byte[] prepared = SvgStaticSnapshot.Create(source, CancellationToken.None);
        Assert.NotSame(source, prepared);
        XmlDocument sanitized;
        using (var input = new MemoryStream(prepared, writable: false))
            sanitized = SvgStaticSnapshot.LoadDocument(input, CancellationToken.None);

        Assert.Empty(sanitized.GetElementsByTagName("script", "http://www.w3.org/2000/svg"));
        Assert.Empty(sanitized.GetElementsByTagName("animate", "http://www.w3.org/2000/svg"));
        XmlElement retained = Assert.IsType<XmlElement>(sanitized.GetElementsByTagName("node", "urn:preserved")[0]);
        Assert.Equal("keep-cdata", retained.InnerText);
        Assert.Contains(sanitized.SelectNodes("//comment()")!.OfType<XmlComment>(),
            static comment => comment.Value == "keep-comment");
        Assert.Contains(sanitized.SelectNodes("//processing-instruction('keep')")!.OfType<XmlProcessingInstruction>(),
            static instruction => instruction.Data == "value");
    }

    [Fact]
    public void CancellationAwareReadStreamChecksTokenOnEveryReadAndDropsItsSourceOnDispose()
    {
        using var cancellation = new CancellationTokenSource();
        (CancellationAwareReadStream stream, WeakReference source) = CreateReadStream(cancellation.Token);
        byte[] buffer = new byte[2];

        Assert.Equal(2, stream.Read(buffer));
        Assert.Equal(new byte[] { 1, 2 }, buffer);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => stream.Read(buffer));

        Assert.True(source.IsAlive);
        stream.Dispose();
        ForceCollection();
        Assert.False(source.IsAlive);
        GC.KeepAlive(stream);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (CancellationAwareReadStream Stream, WeakReference Source) CreateReadStream(
        CancellationToken cancellationToken)
    {
        byte[] source = [1, 2, 3, 4];
        return (new CancellationAwareReadStream(source, cancellationToken), new WeakReference(source));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static XmlReader CreateParser(string source) => XmlReader.Create(
        new StringReader(source),
        new XmlReaderSettings
        {
            Async = false,
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = null,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            MaxCharactersFromEntities = 1,
            MaxCharactersInDocument = SvgResourceBudget.MaxInputBytes,
        });

    private static byte[] CreateNestedSvg(int nestedDepth)
    {
        var source = new StringBuilder("<svg>");
        for (int index = 0; index < nestedDepth; index++)
            source.Append("<g>");
        for (int index = 0; index < nestedDepth; index++)
            source.Append("</g>");
        source.Append("</svg>");
        return Encoding.UTF8.GetBytes(source.ToString());
    }

    private static byte[] CreateWideSvg(int childElementCount)
    {
        var source = new StringBuilder(11 + (childElementCount * 4));
        source.Append("<svg>");
        for (int index = 0; index < childElementCount; index++)
            source.Append("<g/>");
        source.Append("</svg>");
        return Encoding.UTF8.GetBytes(source.ToString());
    }

    private sealed class CancelAfterReadXmlReader(CancellationTokenSource cancellation) : XmlReader
    {
        private bool _read;

        public int ReadCalls { get; private set; }
        public override XmlNodeType NodeType => _read ? XmlNodeType.Element : XmlNodeType.None;
        public override string LocalName => _read ? "svg" : string.Empty;
        public override string NamespaceURI => string.Empty;
        public override string Prefix => string.Empty;
        public override string Value => string.Empty;
        public override int Depth => 0;
        public override string BaseURI => string.Empty;
        public override bool IsEmptyElement => true;
        public override int AttributeCount => 0;
        public override bool EOF => false;
        public override ReadState ReadState => _read ? ReadState.Interactive : ReadState.Initial;
        public override XmlNameTable NameTable { get; } = new NameTable();

        public override string? GetAttribute(string name) => null;
        public override string? GetAttribute(string name, string? namespaceURI) => null;
        public override string GetAttribute(int i) => throw new ArgumentOutOfRangeException(nameof(i));
        public override bool MoveToAttribute(string name) => false;
        public override bool MoveToAttribute(string name, string? namespaceURI) => false;
        public override bool MoveToFirstAttribute() => false;
        public override bool MoveToNextAttribute() => false;
        public override bool MoveToElement() => false;
        public override bool ReadAttributeValue() => false;

        public override bool Read()
        {
            ReadCalls++;
            _read = true;
            cancellation.Cancel();
            return true;
        }

        public override string? LookupNamespace(string prefix) => null;
        public override void ResolveEntity() => throw new NotSupportedException();
        public override void Close() { }
    }

    private sealed class CancelAfterFirstReadStream(
        byte[] source,
        CancellationTokenSource cancellation) : Stream
    {
        private int _position;
        private int _readCount;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (_position >= source.Length || buffer.IsEmpty)
                return 0;

            int count = Math.Min(1, Math.Min(buffer.Length, source.Length - _position));
            source.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            if (Interlocked.Increment(ref _readCount) == 1)
                cancellation.Cancel();
            return count;
        }

        public override int ReadByte()
        {
            Span<byte> single = stackalloc byte[1];
            return Read(single) == 0 ? -1 : single[0];
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
