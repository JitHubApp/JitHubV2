#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.PerformanceHarness;
using Markdig;
using Markdig.Syntax;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;
using Xunit.Abstractions;
using MarkdownDocument = MarkdownRenderer.Document.MarkdownDocument;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Diagnostic only: measures a lower-bound subinterval of the snapshot layout
/// gate held while a completed standalone image is committed through the real
/// root-local image path. It is not a release benchmark, frame-time bound, or
/// Edge/native-memory qualification.
/// </summary>
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class MixedBuiltinSuffixGateDiagnosticTests(ITestOutputHelper output)
{
    private const int CorpusUtf16Bytes = 1_048_576;
    private const float LayoutWidth = 320f;
    private const string ImageUrl = "https://suffix-gate.example.test/diagnostic.png";
    private const string StartMarker = "suffixgate-start-marker-7d28f148";
    private const string EndMarker = "suffixgate-end-marker-91caeb33";
    private const string MixedSectionStart = "## Native rendering and semantic navigation\n\n";
    private static readonly FieldInfo LayoutLockField = typeof(LayoutSnapshot).GetField(
        "_layoutLock", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The snapshot layout lock field changed.");
    private static readonly FieldInfo CodeChunkBottomEdgesField = GetPrivateField(typeof(CodeBlockBox), "_chunkBottomEdges");
    private static readonly FieldInfo CodeVisualLinesField = GetPrivateField(typeof(CodeBlockBox), "_visualLines");
    private static readonly FieldInfo CodeVisualLineBottomEdgesField = GetPrivateField(typeof(CodeBlockBox), "_visualLineBottomEdges");
    private static readonly FieldInfo TableRowHeightsField = GetPrivateField(typeof(TableBox), "_rowHeights");
    private static readonly FieldInfo TableRowBottomEdgesField = GetPrivateField(typeof(TableBox), "_rowBottomEdges");
    private static readonly FieldInfo StackChildBottomEdgesField = GetPrivateField(typeof(StackBox), "_childBottomEdges");

    [Theory]
    [InlineData(PerformanceDocumentFactory.ReadmeMixedCorpus)]
    [InlineData(PerformanceDocumentFactory.WarmScrollStressCorpus)]
    public async Task CompletedRasterMixedBuiltinSuffixGateLowerBoundIsRecorded(string corpusName)
    {
        string body = CreateCorpusBody(corpusName);
        string source = CreateFixtureSource(body);
        string sourceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        byte[] png = DisplaySizedRasterDecodeTests.CreatePng(128, 256);

        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: true);
        Assert.True(seed.ForceSoftwareRenderer);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        RasterNativeWicDiagnosticSnapshot nativeBefore = RasterNativeWicDiagnostics.GetSnapshot();

        // The complete legacy fixture warms the device before the candidate.
        // Retire it before building the candidate to avoid overlapping large graphs.
        FixtureOracle legacyOracle;
        int referenceRoots;
        float referencePlaceholderHeight;
        float referenceCompletedHeight;
        BuiltFixture reference = await BuildFixtureAsync(seed, session, png, source, "legacy-reference");
        try
        {
            AssertRepresentativeBuiltinGraph(reference.Snapshot);
            await LoadCompletedImageAsync(reference.Image);
            referencePlaceholderHeight = (float)reference.Image.Bounds.Height;
            reference.Snapshot.RelayoutChangedBlocks(
                [reference.Image.BlockIndex], LayoutWidth, CancellationToken.None);
            referenceCompletedHeight = (float)reference.Image.Bounds.Height;
            Assert.NotEqual(referencePlaceholderHeight, referenceCompletedHeight);
            referenceRoots = reference.Snapshot.Blocks.Count;
            legacyOracle = CaptureOracle(reference.Snapshot);
        }
        finally
        {
            await reference.DisposeAsync();
        }
        reference = null!;

        RasterNativeWicDiagnosticSnapshot beforeCandidate = RasterNativeWicDiagnostics.GetSnapshot();
        BuiltFixture candidate = await BuildFixtureAsync(seed, session, png, source, "prepared-candidate");
        double arrangeSubloopMilliseconds;
        float candidatePlaceholderHeight;
        float candidateCompletedHeight;
        FixtureOracle candidateOracle;
        try
        {
            AssertRepresentativeBuiltinGraph(candidate.Snapshot);
            await LoadCompletedImageAsync(candidate.Image);
            candidatePlaceholderHeight = (float)candidate.Image.Bounds.Height;
            candidate.GateRecorder.Arm(LayoutLockField.GetValue(candidate.Snapshot)
                ?? throw new InvalidOperationException("The snapshot has no layout gate."));

            MarkdownLayoutContext? constructionContext = null;
            PreparedImageRelayoutResult result;
            try
            {
                result = candidate.Snapshot.TryRelayoutCompletedImageRoot(
                    [candidate.Image.BlockIndex],
                    LayoutWidth,
                    CancellationToken.None,
                    out constructionContext,
                    expectedPointerInputEpoch: candidate.Snapshot.PointerInputEpoch);
            }
            finally
            {
                if (constructionContext is not null)
                {
                    await constructionContext.DrainOwnedRetirementsAsync()
                        .WaitAsync(TimeSpan.FromSeconds(30));
                }
            }

            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            candidateCompletedHeight = (float)candidate.Image.Bounds.Height;
            Assert.NotEqual(candidatePlaceholderHeight, candidateCompletedHeight);
            Assert.NotNull(candidate.Image.Bitmap);
            Assert.False(candidate.Snapshot.GeometryRecoveryRequired);

            GateStampMeasurement stamps = candidate.GateRecorder.Read();
            Assert.True(stamps.StartTimestamp > 0,
                "The start sentinel immediately after the completed image did not run.");
            Assert.True(stamps.EndTimestamp > stamps.StartTimestamp,
                "The end sentinel after the final mixed root did not run after the start sentinel.");
            Assert.True(stamps.StartObservedLayoutLock,
                "The start sentinel was not arranged while the source snapshot layout lock was held.");
            Assert.True(stamps.EndObservedLayoutLock,
                "The end sentinel was not arranged while the source snapshot layout lock was held.");
            arrangeSubloopMilliseconds =
                (stamps.EndTimestamp - stamps.StartTimestamp) * 1000.0 / Stopwatch.Frequency;

            candidateOracle = CaptureOracle(candidate.Snapshot);
            Assert.Equal(legacyOracle, candidateOracle);
            Assert.Equal(referenceRoots, candidate.Snapshot.Blocks.Count);
        }
        finally
        {
            await candidate.DisposeAsync();
        }
        candidate = null!;

        RasterNativeWicDiagnosticSnapshot nativeAfter = RasterNativeWicDiagnostics.GetSnapshot();
        long nativeStarted = nativeAfter.NativeStarted - nativeBefore.NativeStarted;
        long nativePixelLeases = nativeAfter.PixelLeaseCompleted - nativeBefore.PixelLeaseCompleted;
        long candidateNativeStarted = nativeAfter.NativeStarted - beforeCandidate.NativeStarted;
        long candidateNativePixelLeases = nativeAfter.PixelLeaseCompleted - beforeCandidate.PixelLeaseCompleted;
        Assert.True(candidateNativeStarted > 0, "The candidate did not enter the native WIC decoder path.");
        Assert.True(candidateNativePixelLeases > 0, "The candidate did not hand off a native decoded-pixel lease.");

        output.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "diagnostic-not-release-evidence {0} bytes={1} sha={2} roots={3} boxes={4} " +
            "code={5}/{6}chunks/{7}visualLines table={8}/{9}rows stack={10} lists={11} " +
            "raster={12}px/{13}B~ viewportSlots={14} selection={15} hits={16} focus={17} " +
            "height={18:R}->{19:R} reference={20:R}->{21:R} native={22}/{23} candidate={24}/{25} " +
            "gateLowerBoundMs={26:F3} digest={27}",
            corpusName, source.Length * sizeof(char), sourceSha256, candidateOracle.RootCount, candidateOracle.BoxCount,
            candidateOracle.CodeBlockCount, candidateOracle.CodeChunkCount, candidateOracle.CodeVisualLineCount,
            candidateOracle.TableCount, candidateOracle.TableRowCount, candidateOracle.StackCount,
            candidateOracle.ListItemCount, candidateOracle.DecodedRasterPixels,
            candidateOracle.DecodedRasterBytesEstimate, candidateOracle.ViewportSlotCount,
            candidateOracle.SelectionRectCount, candidateOracle.HitQueryCount, candidateOracle.FocusableCount,
            candidatePlaceholderHeight, candidateCompletedHeight, referencePlaceholderHeight,
            referenceCompletedHeight, nativeStarted, nativePixelLeases, candidateNativeStarted,
            candidateNativePixelLeases, arrangeSubloopMilliseconds, candidateOracle.Digest));
    }

    private static string CreateCorpusBody(string corpusName)
    {
        if (string.Equals(corpusName, PerformanceDocumentFactory.WarmScrollStressCorpus, StringComparison.Ordinal))
            return PerformanceDocumentFactory.CreateWarmScrollStress(CorpusUtf16Bytes);

        if (!string.Equals(corpusName, PerformanceDocumentFactory.ReadmeMixedCorpus, StringComparison.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(corpusName));

        string generated = PerformanceDocumentFactory.Create(CorpusUtf16Bytes);
        int incompleteSection = generated.LastIndexOf(MixedSectionStart, StringComparison.Ordinal);
        if (incompleteSection <= 0)
            throw new InvalidOperationException("The README-mixed corpus no longer has a complete section boundary.");

        // PerformanceDocumentFactory.Create intentionally fills an exact UTF-16
        // byte budget and can stop inside a fence. Keep all complete built-in
        // sections, then restore approximately the same source size with plain
        // prose so the trailing marker remains a separate parsed paragraph.
        int targetCharacters = CorpusUtf16Bytes / sizeof(char);
        var builder = new StringBuilder(targetCharacters);
        builder.Append(generated.AsSpan(0, incompleteSection));
        const string prose = "Additional README prose retains ordinary paragraph layout and semantic ranges.\n\n";
        while (builder.Length + prose.Length <= targetCharacters)
            builder.Append(prose);
        int remaining = targetCharacters - builder.Length;
        if (remaining > 0)
            builder.Append(prose.AsSpan(0, remaining));
        return builder.ToString().TrimEnd();
    }

    private static string CreateFixtureSource(string body) =>
        $"![completed diagnostic raster]({ImageUrl})\n\n" +
        $"{StartMarker}\n\n" +
        body.TrimEnd() +
        $"\n\n{EndMarker}\n";

    private static async Task<BuiltFixture> BuildFixtureAsync(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        byte[] png,
        string source,
        string cachePartition)
    {
        var gateRecorder = new GateStampRecorder();
        var renderer = new GateStampParagraphRenderer(source, gateRecorder);
        var registry = new MarkdownExtensionRegistry()
            .ConfigureGitHubReadmeRegistry()
            .RegisterRenderer<ParagraphBlock>(renderer);
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(source, registry.BuildPipeline());
        MarkdownDocument semanticDocument = MarkdownDocument.FromParsed(source, parsed);
        var sourceMap = new MarkdownSourceMap(source);
        var context = new MarkdownLayoutContext(
            seed.Device,
            seed.SourceContext.ThemeSnapshot,
            sourceMap,
            registry,
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            CancellationToken = CancellationToken.None,
            ImageCancellationToken = CancellationToken.None,
            ImagePreparationOwner = seed.ImagePreparationOwnerToken,
            PerformanceDocumentOwner = new object(),
            PerformanceSession = session,
            ImageResolver = new FixedPngResolver(png, $"suffix-gate-{cachePartition}-{Guid.NewGuid():N}"),
            AllowThirdPartyRemoteImages = true,
            RasterizationScale = 1f,
        };

        LayoutSnapshot? snapshot = null;
        try
        {
            snapshot = new LayoutBuilder(context, semanticDocument: semanticDocument)
                .Build(parsed, LayoutWidth, CancellationToken.None);
            gateRecorder.AttachLayoutLock(LayoutLockField.GetValue(snapshot)
                ?? throw new InvalidOperationException("The snapshot has no layout gate."));

            BlockBox[] roots = snapshot.Blocks.ToArray();
            ImageBox image = Assert.Single(roots.OfType<ImageBox>());
            GateStampBox start = renderer.StartStamp
                ?? throw new InvalidOperationException("The start marker renderer did not run.");
            GateStampBox end = renderer.EndStamp
                ?? throw new InvalidOperationException("The end marker renderer did not run.");
            int imageOrdinal = Array.IndexOf(roots, image);
            int startOrdinal = Array.IndexOf(roots, start);
            int endOrdinal = Array.IndexOf(roots, end);
            Assert.Equal(imageOrdinal + 1, startOrdinal);
            Assert.Equal(snapshot.Blocks.Count - 1, endOrdinal);
            return new BuiltFixture(context, snapshot, image, gateRecorder);
        }
        catch
        {
            try
            {
                if (snapshot is not null)
                    await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            throw;
        }
    }

    private static async Task LoadCompletedImageAsync(ImageBox image)
    {
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image).WaitAsync(TimeSpan.FromSeconds(30));
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => image.ActiveImageWorkCountForTests == 0).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(image.Bitmap);
    }

    private static void AssertRepresentativeBuiltinGraph(LayoutSnapshot snapshot)
    {
        Assert.Contains(snapshot.Blocks, static block => block is CodeBlockBox);
        Assert.Contains(snapshot.Blocks, static block => block is TableBox);
        Assert.Contains(snapshot.Blocks, static block => block is StackBox);
        Assert.Contains(snapshot.Blocks, static block => block is ImageBox);
    }

    private static FixtureOracle CaptureOracle(LayoutSnapshot snapshot)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        int boxCount = 0;
        int codeBlockCount = 0;
        int codeChunkCount = 0;
        int codeVisualLineCount = 0;
        int tableCount = 0;
        int tableRowCount = 0;
        int stackCount = 0;
        int listItemCount = 0;
        long decodedRasterPixels = 0;
        long decodedRasterBytesEstimate = 0;

        writer.Write(0x47415445); // "GATE" section marker.
        writer.Write(snapshot.Blocks.Count);
        WriteDouble(writer, snapshot.Size.Width);
        WriteDouble(writer, snapshot.Size.Height);

        var seen = new HashSet<BlockBox>(ReferenceEqualityComparer.Instance);
        for (int ordinal = 0; ordinal < snapshot.Blocks.Count; ordinal++)
            CaptureBox(snapshot.Blocks[ordinal], -1);

        writer.Write(0x494E4458); // Viewport-index section.
        int viewportSlotCount;
        if (!snapshot.TryBeginPaint())
            throw new InvalidOperationException("The reference snapshot was not available for index inspection.");
        try
        {
            Rect allDocument = new(0, 0, snapshot.Size.Width, snapshot.Size.Height + 1);
            ViewportRange range = snapshot.GetMeasuredViewportRangeUnderPaint(allDocument);
            viewportSlotCount = range.End - range.Start;
            writer.Write(range.Start);
            writer.Write(range.End);
            for (int slot = range.Start; slot < range.End; slot++)
            {
                int ordinal = snapshot.GetBlockOrdinalAtViewportSlotUnderPaint(slot);
                bool resolved = snapshot.TryGetMeasuredViewportBlockAtSlotUnderPaint(slot, allDocument, out BlockBox block);
                writer.Write(slot);
                writer.Write(ordinal);
                writer.Write(resolved);
                writer.Write(resolved ? block.BlockIndex : 0);
                if (resolved)
                    WriteRect(writer, block.Bounds);
            }
        }
        finally
        {
            snapshot.EndPaint();
        }

        InlineContainerBox sample = snapshot.Blocks.OfType<InlineContainerBox>().First();
        writer.Write(0x53454C45); // Representative inline selection geometry.
        var sampleRange = new DocumentRange(
            new DocumentPosition(sample.BlockIndex, 0, 0),
            new DocumentPosition(sample.BlockIndex, int.MaxValue, int.MaxValue));
        int selectionRectCount = 0;
        Rect[] sampleSelection = sample.GetSelectionRects(sampleRange).ToArray();
        Assert.NotEmpty(sampleSelection);
        writer.Write(sampleSelection.Length);
        foreach (Rect rect in sampleSelection)
        {
            WriteRect(writer, rect);
            selectionRectCount++;
        }

        writer.Write(0x48495454); // Representative center hit-test.
        Rect sampleBounds = sampleSelection[0];
        var samplePoint = new Point(
            sampleBounds.X + sampleBounds.Width / 2,
            sampleBounds.Y + sampleBounds.Height / 2);
        bool sampleHit = snapshot.HitTest(samplePoint, out DocumentPosition samplePosition);
        Assert.True(sampleHit, "The point inside a selected text rectangle should resolve through snapshot hit testing.");
        WriteDouble(writer, samplePoint.X);
        WriteDouble(writer, samplePoint.Y);
        writer.Write(sampleHit);
        writer.Write(samplePosition.BlockIndex);
        writer.Write(samplePosition.InlineIndex);
        writer.Write(samplePosition.CharacterOffset);
        int hitQueryCount = 1;

        writer.Write(0x464F4355); // Focusable item identity/order section.
        IReadOnlyList<FocusableItem> focusableItems = snapshot.CollectFocusableItems();
        foreach (FocusableItem item in focusableItems)
        {
            writer.Write(item.BlockIndex);
            writer.Write(item.InlineIndex);
            writer.Write((int)item.Kind);
        }

        writer.Write(0x434E5453); // Scalar inventory section.
        writer.Write(boxCount);
        writer.Write(codeBlockCount);
        writer.Write(codeChunkCount);
        writer.Write(codeVisualLineCount);
        writer.Write(tableCount);
        writer.Write(tableRowCount);
        writer.Write(stackCount);
        writer.Write(listItemCount);
        writer.Write(decodedRasterPixels);
        writer.Write(decodedRasterBytesEstimate);
        writer.Write(selectionRectCount);
        writer.Write(hitQueryCount);
        writer.Write(focusableItems.Count);
        writer.Flush();

        string digest = Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
        return new FixtureOracle(
            snapshot.Blocks.Count,
            boxCount,
            codeBlockCount,
            codeChunkCount,
            codeVisualLineCount,
            tableCount,
            tableRowCount,
            stackCount,
            listItemCount,
            decodedRasterPixels,
            decodedRasterBytesEstimate,
            selectionRectCount,
            hitQueryCount,
            viewportSlotCount,
            focusableItems.Count,
            digest);

        void CaptureBox(BlockBox box, int parentOrdinal)
        {
            if (!seen.Add(box))
                return;

            int preorder = boxCount++;
            writer.Write(preorder);
            writer.Write(parentOrdinal);
            writer.Write(box.BlockIndex);
            writer.Write(StableTypeHash(box.GetType()));
            writer.Write(box.IsDirty);
            WriteRect(writer, box.Bounds);

            switch (box)
            {
                case CodeBlockBox code:
                    codeBlockCount++;
                    codeChunkCount += code.Chunks.Count;
                    codeVisualLineCount += code.IndexedVisualLineCount;
                    writer.Write(code.ShowLineNumbers);
                    writer.Write(code.IsCopyButtonEnabled);
                    WriteRect(writer, code.CopyButtonBounds);
                    WriteDouble(writer, code.HorizontalOffset);
                    WriteDouble(writer, code.HorizontalExtent);
                    WriteDouble(writer, code.HorizontalViewport);
                    WriteDoubleArray(writer, (double[])CodeChunkBottomEdgesField.GetValue(code)!);
                    WriteVisualLineArrays(writer, code);
                    for (int chunk = 0; chunk < code.Chunks.Count; chunk++)
                        CaptureBox(code.Chunks[chunk], preorder);
                    break;
                case TableBox table:
                    tableCount++;
                    tableRowCount += table.RowCount;
                    WriteSingleArray(writer, (float[]?)TableRowHeightsField.GetValue(table));
                    WriteDoubleArray(writer, (double[])TableRowBottomEdgesField.GetValue(table)!);
                    WriteDouble(writer, table.HorizontalOffset);
                    WriteDouble(writer, table.HorizontalExtent);
                    WriteDouble(writer, table.HorizontalViewport);
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                        CaptureBox(cell, preorder);
                    break;
                case ListItemBox listItem:
                    listItemCount++;
                    WriteSingle(writer, listItem.MarkerWidth);
                    writer.Write((int)listItem.FlowDirection);
                    CaptureBox(listItem.Marker, preorder);
                    CaptureBox(listItem.Content, preorder);
                    break;
                case StackBox stack:
                    stackCount++;
                    WriteDoubleArray(writer, (double[])StackChildBottomEdgesField.GetValue(stack)!);
                    for (int child = 0; child < stack.Children.Count; child++)
                        CaptureBox(stack.Children[child], preorder);
                    break;
                case InlineContainerBox inline:
                    foreach (InlineRun run in inline.Runs)
                    {
                        if (run is InlineImageRun imageRun)
                            CaptureBox(imageRun.Image, preorder);
                    }
                    break;
                case ImageBox image:
                    if (image.Bitmap is { } bitmap)
                    {
                        var pixels = bitmap.SizeInPixels;
                        long count = checked((long)pixels.Width * pixels.Height);
                        decodedRasterPixels += count;
                        decodedRasterBytesEstimate = checked(decodedRasterBytesEstimate + count * 4);
                    }
                    break;
            }
        }
    }

    private static void WriteVisualLineArrays(BinaryWriter writer, CodeBlockBox code)
    {
        Array visualLines = (Array)CodeVisualLinesField.GetValue(code)!;
        Type lineType = visualLines.GetType().GetElementType()
            ?? throw new InvalidOperationException("Code visual-line entries have no element type.");
        FieldInfo logicalIndex = GetPrivateField(lineType, "LogicalLineIndex");
        FieldInfo top = GetPrivateField(lineType, "Top");
        FieldInfo height = GetPrivateField(lineType, "Height");
        writer.Write(visualLines.Length);
        for (int index = 0; index < visualLines.Length; index++)
        {
            object line = visualLines.GetValue(index)
                ?? throw new InvalidOperationException("A code visual-line entry was null.");
            writer.Write((int)logicalIndex.GetValue(line)!);
            WriteDouble(writer, (double)top.GetValue(line)!);
            WriteDouble(writer, (double)height.GetValue(line)!);
        }
        WriteDoubleArray(writer, (double[])CodeVisualLineBottomEdgesField.GetValue(code)!);
    }

    private static void WriteDoubleArray(BinaryWriter writer, double[] values)
    {
        writer.Write(values.Length);
        foreach (double value in values)
            WriteDouble(writer, value);
    }

    private static void WriteSingleArray(BinaryWriter writer, float[]? values)
    {
        writer.Write(values?.Length ?? -1);
        if (values is null)
            return;
        foreach (float value in values)
            WriteSingle(writer, value);
    }

    private static void WriteSingle(BinaryWriter writer, float value)
        => writer.Write(BitConverter.SingleToInt32Bits(value));

    private static FieldInfo GetPrivateField(Type type, string name)
        => type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"The required private field {type.Name}.{name} changed.");

    private static ulong StableTypeHash(Type type)
    {
        string name = type.FullName ?? type.Name;
        ulong hash = 14_695_981_039_346_656_037UL;
        foreach (char value in name)
        {
            hash ^= value;
            hash *= 1_099_511_628_211UL;
        }
        return hash;
    }

    private static void WriteRect(BinaryWriter writer, Rect rect)
    {
        WriteDouble(writer, rect.X);
        WriteDouble(writer, rect.Y);
        WriteDouble(writer, rect.Width);
        WriteDouble(writer, rect.Height);
    }

    private static void WriteDouble(BinaryWriter writer, double value)
        => writer.Write(BitConverter.DoubleToInt64Bits(value));

    private sealed class GateStampParagraphRenderer(string source, GateStampRecorder recorder)
        : MarkdownNodeRenderer<ParagraphBlock>
    {
        private readonly string _source = source;
        private readonly GateStampRecorder _recorder = recorder;

        internal GateStampBox? StartStamp { get; private set; }
        internal GateStampBox? EndStamp { get; private set; }

        public override BlockBox? BuildBlock(ParagraphBlock node, MarkdownLayoutContext context)
        {
            if (IsLiteralMarker(node, StartMarker))
                return StartStamp = new GateStampBox(_recorder, isStart: true);
            if (IsLiteralMarker(node, EndMarker))
                return EndStamp = new GateStampBox(_recorder, isStart: false);
            return null;
        }

        private bool IsLiteralMarker(ParagraphBlock node, string marker)
        {
            int start = node.Span.Start;
            int length = node.Span.Length;
            if (start < 0 || length < 0 || start > _source.Length - length)
                return false;
            return _source.AsSpan(start, length).Trim().SequenceEqual(marker.AsSpan());
        }
    }

    private sealed class GateStampBox(GateStampRecorder recorder, bool isStart) : BlockBox
    {
        private readonly GateStampRecorder _recorder = recorder;
        private readonly bool _isStart = isStart;

        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(0, 0, availableWidth, 0);
            return 0;
        }

        public override void Arrange(float x, float y, float width)
        {
            base.Arrange(x, y, width);
            _recorder.Record(_isStart);
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
    }

    private sealed class GateStampRecorder
    {
        private object? _layoutLock;
        private bool _armed;
        private long _startTimestamp;
        private long _endTimestamp;
        private bool _startObservedLayoutLock;
        private bool _endObservedLayoutLock;

        internal void AttachLayoutLock(object layoutLock) => _layoutLock = layoutLock;

        internal void Arm(object layoutLock)
        {
            _layoutLock = layoutLock;
            _startTimestamp = 0;
            _endTimestamp = 0;
            _startObservedLayoutLock = false;
            _endObservedLayoutLock = false;
            _armed = true;
        }

        internal void Record(bool isStart)
        {
            if (!_armed)
                return;

            long timestamp = Stopwatch.GetTimestamp();
            bool layoutLockHeld = Monitor.IsEntered(_layoutLock!);
            if (isStart)
            {
                _startTimestamp = timestamp;
                _startObservedLayoutLock = layoutLockHeld;
            }
            else
            {
                _endTimestamp = timestamp;
                _endObservedLayoutLock = layoutLockHeld;
            }
        }

        internal GateStampMeasurement Read() => new(
            _startTimestamp,
            _endTimestamp,
            _startObservedLayoutLock,
            _endObservedLayoutLock);
    }

    private sealed class FixedPngResolver(byte[] bytes, string cacheKey) : IMarkdownImageResolver
    {
        private readonly byte[] _bytes = bytes;
        private readonly string _cacheKey = cacheKey;

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(_bytes, "image/png", CacheKey: _cacheKey)));
        }
    }

    private sealed class BuiltFixture(
        MarkdownLayoutContext context,
        LayoutSnapshot snapshot,
        ImageBox image,
        GateStampRecorder gateRecorder) : IAsyncDisposable
    {
        private int _disposed;

        internal MarkdownLayoutContext Context { get; } = context;
        internal LayoutSnapshot Snapshot { get; } = snapshot;
        internal ImageBox Image { get; } = image;
        internal GateStampRecorder GateRecorder { get; } = gateRecorder;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                await Snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                await Context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }

    private readonly record struct GateStampMeasurement(
        long StartTimestamp,
        long EndTimestamp,
        bool StartObservedLayoutLock,
        bool EndObservedLayoutLock);

    private readonly record struct FixtureOracle(
        int RootCount,
        int BoxCount,
        int CodeBlockCount,
        int CodeChunkCount,
        int CodeVisualLineCount,
        int TableCount,
        int TableRowCount,
        int StackCount,
        int ListItemCount,
        long DecodedRasterPixels,
        long DecodedRasterBytesEstimate,
        int SelectionRectCount,
        int HitQueryCount,
        int ViewportSlotCount,
        int FocusableCount,
        string Digest);
}
#endif
