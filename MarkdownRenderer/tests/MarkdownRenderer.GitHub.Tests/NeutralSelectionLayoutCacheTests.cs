using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class NeutralSelectionLayoutCacheTests(ITestOutputHelper output)
{
    private const float Width = 320f;
    private const string Text =
        "Selection endpoint motion should reuse one neutral native layout across wrapped lines. " +
        "Mixed direction text includes Hebrew שלום beside Latin words and more content for raster parity.";
    private static readonly Color SelectionColor = Color.FromArgb(0xFF, 0x28, 0x64, 0x9A);
    private static readonly Color AlternateSelectionColor = Color.FromArgb(0xFF, 0xA1, 0x4C, 0x20);
    private static readonly FieldInfo SelectionLayoutField =
        typeof(InlineContainerBox).GetField("_selectionLayout", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not find InlineContainerBox._selectionLayout.");

    [Fact]
    public void SelectionRangeChangesReuseNativeLayoutAndRetirementRecord()
    {
        using var harness = new Harness(FlowDirection.LeftToRight);
        var retirement = new NativeResourceRetirement(createTimer: false);
        InlineContainerBox box = harness.Own(CreateTextRoot(harness.CreateContext(), retirement));

        PaintSelection(box, harness.Device, FindRange(box, "Selection"), SelectionColor);
        CanvasTextLayout selectionLayout = SelectionLayout(box);
        NativeResourceRetirementRecord retirementRecord = SelectionRetirementRecord(box);

        foreach (string token in new[] { "endpoint", "motion", "reuse", "wrapped", "Hebrew", "שלום", "Latin", "raster" })
        {
            PaintSelection(box, harness.Device, FindRange(box, token), SelectionColor);
            Assert.Same(selectionLayout, SelectionLayout(box));
            Assert.Same(retirementRecord, SelectionRetirementRecord(box));
            Assert.Equal(0, retirement.PendingRecordCount);
            Assert.Equal(0, retirement.PendingResourceCount);
        }
    }

    [Fact]
    public void PreparedPlacementTransferStaysCurrentAcrossSelectionEndpointChanges()
    {
        using var harness = new Harness(FlowDirection.LeftToRight);
        var retirement = new NativeResourceRetirement(createTimer: false);
        InlineContainerBox source = harness.Own(CreateTextRoot(harness.CreateContext(), retirement));
        PaintSelection(source, harness.Device, FindRange(source, "Selection"), SelectionColor);
        CanvasTextLayout selectionLayout = SelectionLayout(source);
        NativeResourceRetirementRecord retirementRecord = SelectionRetirementRecord(source);

        Assert.True(source.TryCreatePrivateInlinePlacementClone(
            harness.CreateContext(),
            Width,
            out InlineContainerBox? prepared,
            out _));
        InlineContainerBox candidate = harness.Own(Assert.IsType<InlineContainerBox>(prepared));
        Assert.True(source.TryCaptureContainerLeafMeasuredPlacementTransfer(
            candidate,
            Width,
            out InlineContainerBox.MeasuredPlacementTransfer? transfer));
        Assert.NotNull(transfer);

        _ = candidate.Measure(Width);
        candidate.Arrange(0, 0, Width);
        Assert.True(transfer!.IsCurrentAndPrepared());

        foreach (string token in new[] { "endpoint", "motion", "reuse", "wrapped", "Hebrew", "שלום", "Latin", "raster" })
        {
            PaintSelection(source, harness.Device, FindRange(source, token), SelectionColor);
            Assert.Same(selectionLayout, SelectionLayout(source));
            Assert.Same(retirementRecord, SelectionRetirementRecord(source));
            Assert.True(transfer.IsCurrentAndPrepared());
        }
    }

    [Fact]
    public void PrimaryTextAndWidthReplacementsRetireAndRecreateSelectionLayout()
    {
        using var harness = new Harness(FlowDirection.LeftToRight);
        var retirement = new NativeResourceRetirement(createTimer: false);
        InlineContainerBox box = harness.Own(CreateTextRoot(harness.CreateContext(), retirement));
        PaintSelection(box, harness.Device, FindRange(box, "Selection"), SelectionColor);
        CanvasTextLayout initialPrimary = PrimaryLayout(box);
        CanvasTextLayout initialSelection = SelectionLayout(box);
        NativeResourceRetirementRecord initialSelectionRecord = SelectionRetirementRecord(box);

        box.Add(new TextRun(" Text appended to replace the complete primary buffer.")
        {
            SourceSpan = new SourceSpan(0, 0),
        });
        _ = box.Measure(Width);
        box.Arrange(0, 0, Width);

        Assert.NotSame(initialPrimary, PrimaryLayout(box));
        Assert.Null(NullableField(box, "_selectionLayout"));
        Assert.Null(NullableField(box, "_selectionLayoutRetirementRecord"));
        PaintSelection(box, harness.Device, FindRange(box, "endpoint"), SelectionColor);
        CanvasTextLayout textReflowSelection = SelectionLayout(box);
        NativeResourceRetirementRecord textReflowRecord = SelectionRetirementRecord(box);
        Assert.NotSame(initialSelection, textReflowSelection);
        Assert.NotSame(initialSelectionRecord, textReflowRecord);

        CanvasTextLayout textReflowPrimary = PrimaryLayout(box);
        _ = box.Measure(Width - 48f);
        box.Arrange(0, 0, Width - 48f);

        Assert.NotSame(textReflowPrimary, PrimaryLayout(box));
        Assert.Null(NullableField(box, "_selectionLayout"));
        Assert.Null(NullableField(box, "_selectionLayoutRetirementRecord"));
        PaintSelection(box, harness.Device, FindRange(box, "wrapped"), SelectionColor);
        Assert.NotSame(textReflowSelection, SelectionLayout(box));
        Assert.NotSame(textReflowRecord, SelectionRetirementRecord(box));
    }

    [Fact]
    public void SelectionColorChangeBuildsAReplacementNeutralLayout()
    {
        using var harness = new Harness(FlowDirection.LeftToRight);
        var retirement = new NativeResourceRetirement(createTimer: false);
        InlineContainerBox box = harness.Own(CreateTextRoot(harness.CreateContext(), retirement));
        PaintSelection(box, harness.Device, FindRange(box, "Selection"), SelectionColor);
        CanvasTextLayout first = SelectionLayout(box);
        NativeResourceRetirementRecord firstRecord = SelectionRetirementRecord(box);

        PaintSelection(box, harness.Device, FindRange(box, "endpoint"), AlternateSelectionColor);

        Assert.NotSame(first, SelectionLayout(box));
        Assert.NotSame(firstRecord, SelectionRetirementRecord(box));
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
    }

    [Fact]
    public void FirstNeutralSelectionLayoutCreationAfterContextCancellationDoesNotThrow()
    {
        using var cancellation = new CancellationTokenSource();
        using var harness = new Harness(FlowDirection.LeftToRight);
        var retirement = new NativeResourceRetirement(createTimer: false);
        InlineContainerBox box = harness.Own(CreateTextRoot(harness.CreateContext(cancellation.Token), retirement));
        CanvasTextLayout primary = PrimaryLayout(box);

        cancellation.Cancel();
        PaintSelection(box, harness.Device, FindRange(box, "Selection"), SelectionColor);

        Assert.Same(primary, PrimaryLayout(box));
        Assert.NotNull(SelectionLayout(box));
        Assert.NotNull(SelectionRetirementRecord(box));
    }

    [Fact]
    public void CachedNeutralSelectionLayoutRemainsReusableAfterContextCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var harness = new Harness(FlowDirection.LeftToRight);
        var retirement = new NativeResourceRetirement(createTimer: false);
        InlineContainerBox box = harness.Own(CreateTextRoot(harness.CreateContext(cancellation.Token), retirement));
        PaintSelection(box, harness.Device, FindRange(box, "Selection"), SelectionColor);
        CanvasTextLayout primary = PrimaryLayout(box);
        CanvasTextLayout selection = SelectionLayout(box);
        NativeResourceRetirementRecord selectionRecord = SelectionRetirementRecord(box);

        cancellation.Cancel();
        foreach (string token in new[] { "endpoint", "wrapped", "שלום", "raster" })
        {
            PaintSelection(box, harness.Device, FindRange(box, token), SelectionColor);
            Assert.Same(primary, PrimaryLayout(box));
            Assert.Same(selection, SelectionLayout(box));
            Assert.Same(selectionRecord, SelectionRetirementRecord(box));
        }
    }

    [Theory]
    [InlineData(96f, false)]
    [InlineData(144f, false)]
    [InlineData(192f, false)]
    [InlineData(96f, true)]
    [InlineData(144f, true)]
    [InlineData(192f, true)]
    public void ReusedNeutralLayoutMatchesFreshSelectionPixelsAndGeometryAcrossDpiAndFlow(
        float dpi,
        bool rightToLeft)
    {
        FlowDirection flowDirection = rightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        using var harness = new Harness(flowDirection, fontSize: 17f);
        var retirement = new NativeResourceRetirement(createTimer: false);
        InlineContainerBox reused = harness.Own(CreateTextRoot(harness.CreateContext(), retirement));
        InlineContainerBox fresh = harness.Own(CreateTextRoot(harness.CreateContext(), retirement));

        PaintSelection(reused, harness.Device, FindRange(reused, "Selection"), SelectionColor);
        foreach (string token in new[] { "endpoint", "motion", "wrapped", "Hebrew", "שלום", "raster" })
            PaintSelection(reused, harness.Device, FindRange(reused, token), SelectionColor);

        DocumentRange reusedRange = FindRange(reused, "Latin");
        DocumentRange freshRange = FindRange(fresh, "Latin");
        Rect[] reusedGeometry = reused.GetSelectionRects(reusedRange).ToArray();
        Rect[] freshGeometry = fresh.GetSelectionRects(freshRange).ToArray();
        AssertRectArraysNearlyEqual(freshGeometry, reusedGeometry);

        byte[] actualPixels = RenderSelection(reused, harness.Device, reusedRange, SelectionColor, dpi);
        byte[] expectedPixels = RenderSelection(fresh, harness.Device, freshRange, SelectionColor, dpi);
        Assert.Equal(expectedPixels, actualPixels);
        Assert.Contains(actualPixels, static value => value != 0);
    }

    [Fact]
    public void NativeSelectionLayoutCreationChurnDiagnosticIsOptIn()
    {
        const int iterations = 120;
        const string environmentVariable = "MARKDOWN_RENDERER_SELECTION_LAYOUT_EXPECTED_CREATION_MODE";
        string? expectedMode = Environment.GetEnvironmentVariable(environmentVariable);
        if (expectedMode is not ("range-churn" or "range-reuse"))
        {
            output.WriteLine(
                "Diagnostic not run; set {0}=range-churn or range-reuse to classify this binary.",
                environmentVariable);
            return;
        }

        using var harness = new Harness(FlowDirection.LeftToRight, countDeviceAccesses: true);
        var retirement = new NativeResourceRetirement(createTimer: false);
        InlineContainerBox box = harness.Own(CreateTextRoot(harness.CreateContext(), retirement));
        DocumentRange firstRange = FindRange(box, "Selection");
        DocumentRange secondRange = FindRange(box, "wrapped");
        PaintSelection(box, harness.Device, firstRange, SelectionColor);

        using var target = new CanvasRenderTarget(
            harness.Device,
            Width,
            System.Math.Max(64f, (float)box.Bounds.Height),
            96f);
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        CanvasTextLayout previousLayout = SelectionLayout(box);
        int deviceAccessesBefore = harness.ResourceCreator.DeviceAccessCount;
        long allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        long startTimestamp = Stopwatch.GetTimestamp();
        int nativeLayoutReplacements = 0;
        for (int index = 0; index < iterations; index++)
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            DocumentRange range = (index & 1) == 0 ? secondRange : firstRange;
            box.PaintSelectionForeground(drawing, range, SelectionColor, Viewport(box));
            CanvasTextLayout currentLayout = GetSelectionLayout(SelectionLayoutField, box);
            if (!ReferenceEquals(previousLayout, currentLayout))
            {
                nativeLayoutReplacements++;
                previousLayout = currentLayout;
            }
        }
        long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore;
        int nativeLayoutCreationSignals = harness.ResourceCreator.DeviceAccessCount - deviceAccessesBefore;
        double elapsedMilliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        output.WriteLine(
            "mode={0}; scope=cpu-command-submission-only; gpu-completion=false; releaseEvidence=false; paints={1}; selection-layout-replacements={2}; creator-device-accesses={3}; managed-bytes={4}; elapsed-ms={5:F3}",
            expectedMode,
            iterations,
            nativeLayoutReplacements,
            nativeLayoutCreationSignals,
            allocatedBytes,
            elapsedMilliseconds);

        if (expectedMode == "range-churn")
            Assert.Equal(iterations, nativeLayoutReplacements);
        else
            Assert.Equal(0, nativeLayoutReplacements);

        // Geometry and raster oracles run after the timed/allocated region.
        InlineContainerBox fresh = harness.Own(CreateTextRoot(harness.CreateContext(), retirement));
        byte[] actualPixels = RenderSelection(box, harness.Device, secondRange, SelectionColor, 96f);
        DocumentRange freshSecondRange = FindRange(fresh, "wrapped");
        byte[] expectedPixels = RenderSelection(fresh, harness.Device, freshSecondRange, SelectionColor, 96f);
        Assert.Equal(expectedPixels, actualPixels);
        AssertRectArraysNearlyEqual(
            fresh.GetSelectionRects(freshSecondRange).ToArray(),
            box.GetSelectionRects(secondRange).ToArray());
    }

    private static InlineContainerBox CreateTextRoot(
        MarkdownLayoutContext context,
        NativeResourceRetirement retirement)
    {
        var box = new InlineContainerBox(
            context,
            MarkdownElementKeys.Body,
            retirement,
            static resource => resource.Dispose())
        {
            BlockIndex = context.NextBlockIndex(),
            UseContainerMargin = false,
            UseContainerPadding = false,
        };
        box.Add(new TextRun(Text)
        {
            SourceSpan = new SourceSpan(0, Text.Length),
        });
        _ = box.Measure(Width);
        box.Arrange(0, 0, Width);
        return box;
    }

    private static DocumentRange FindRange(InlineContainerBox box, string token)
    {
        int start = Text.IndexOf(token, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing test token: {token}");
        return new DocumentRange(
            new DocumentPosition(box.BlockIndex, 0, start),
            new DocumentPosition(box.BlockIndex, 0, start + token.Length));
    }

    private static void PaintSelection(
        InlineContainerBox box,
        CanvasDevice device,
        DocumentRange range,
        Color color)
    {
        using var target = new CanvasRenderTarget(device, Width, System.Math.Max(64f, (float)box.Bounds.Height), 96f);
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        drawing.Clear(Color.FromArgb(0, 0, 0, 0));
        box.PaintSelectionForeground(drawing, range, color, Viewport(box));
    }

    private static byte[] RenderSelection(
        InlineContainerBox box,
        CanvasDevice device,
        DocumentRange range,
        Color color,
        float dpi)
    {
        using var target = new CanvasRenderTarget(
            device,
            Width,
            System.Math.Max(64f, (float)box.Bounds.Height),
            dpi);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            box.PaintSelectionForeground(drawing, range, color, Viewport(box));
        }
        return target.GetPixelBytes();
    }

    private static Rect Viewport(InlineContainerBox box)
        => new(0, 0, Width, System.Math.Max(64f, box.Bounds.Height));

    private static CanvasTextLayout PrimaryLayout(InlineContainerBox box)
        => Field<CanvasTextLayout>(box, "_layout");

    private static CanvasTextLayout SelectionLayout(InlineContainerBox box)
        => Field<CanvasTextLayout>(box, "_selectionLayout");

    private static NativeResourceRetirementRecord SelectionRetirementRecord(InlineContainerBox box)
        => Field<NativeResourceRetirementRecord>(box, "_selectionLayoutRetirementRecord");

    private static T Field<T>(object instance, string name)
        where T : class =>
        (T)(NullableField(instance, name)
            ?? throw new InvalidOperationException($"Expected {name} to contain a {typeof(T).Name}."));

    private static CanvasTextLayout GetSelectionLayout(FieldInfo field, InlineContainerBox box)
        => (CanvasTextLayout)(field.GetValue(box)
            ?? throw new InvalidOperationException("Expected the neutral selection layout to remain cached."));

    private static object? NullableField(object instance, string name)
        => typeof(InlineContainerBox).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);

    private static void AssertRectArraysNearlyEqual(IReadOnlyList<Rect> expected, IReadOnlyList<Rect> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.InRange(System.Math.Abs(expected[index].X - actual[index].X), 0, 0.01);
            Assert.InRange(System.Math.Abs(expected[index].Y - actual[index].Y), 0, 0.01);
            Assert.InRange(System.Math.Abs(expected[index].Width - actual[index].Width), 0, 0.01);
            Assert.InRange(System.Math.Abs(expected[index].Height - actual[index].Height), 0, 0.01);
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly List<InlineContainerBox> _ownedBoxes = new();
        private readonly MarkdownSourceMap _sourceMap = new(Text);
        private readonly MarkdownExtensionRegistry _registry = new();
        private readonly IReadOnlyDictionary<string, bool> _disclosureStates =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly ThemeSnapshot _theme;

        internal Harness(
            FlowDirection flowDirection,
            float fontSize = 16f,
            bool countDeviceAccesses = false)
        {
            Device = new CanvasDevice(forceSoftwareRenderer: true);
            ResourceCreator = new CountingResourceCreator(Device, countDeviceAccesses);
            var body = new ElementStyle
            {
                FontFamily = "Segoe UI",
                FontSize = fontSize,
            };
            var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = body,
                [MarkdownElementKeys.Strong] = body,
                [MarkdownElementKeys.Emphasis] = body,
                [MarkdownElementKeys.Math] = body,
                [MarkdownElementKeys.Link] = body,
                [MarkdownElementKeys.ImageCaption] = body,
            };
            Color transparent = Color.FromArgb(0, 0, 0, 0);
            _theme = new ThemeSnapshot(
                styles,
                new Dictionary<string, ElementStyleOverride>(),
                transparent,
                transparent,
                transparent,
                transparent,
                isDark: false,
                isHighContrast: false,
                textScaleFactor: 1f);
            FlowDirection = flowDirection;
        }

        internal CanvasDevice Device { get; }
        internal CountingResourceCreator ResourceCreator { get; }
        private FlowDirection FlowDirection { get; }

        internal InlineContainerBox Own(InlineContainerBox box)
        {
            _ownedBoxes.Add(box);
            return box;
        }

        internal MarkdownLayoutContext CreateContext(CancellationToken cancellationToken = default) =>
            new(ResourceCreator, _theme, _sourceMap, _registry, FlowDirection, language: "en-US")
            {
                DisclosureStates = _disclosureStates,
                CancellationToken = cancellationToken,
            };

        public void Dispose()
        {
            Exception? disposalFailure = null;
            for (int index = _ownedBoxes.Count - 1; index >= 0; index--)
            {
                try
                {
                    _ownedBoxes[index].Dispose();
                }
                catch (Exception exception)
                {
                    disposalFailure ??= exception;
                }
            }
            _ownedBoxes.Clear();

            try
            {
                SharedCanvasBitmapCache.ReleaseDevice(Device);
            }
            finally
            {
                Device.Dispose();
            }

            if (disposalFailure is not null)
                ExceptionDispatchInfo.Capture(disposalFailure).Throw();
        }
    }

    private sealed class CountingResourceCreator(CanvasDevice device, bool enabled) : ICanvasResourceCreator
    {
        private int _deviceAccessCount;

        internal int DeviceAccessCount => Volatile.Read(ref _deviceAccessCount);

        public CanvasDevice Device
        {
            get
            {
                if (enabled)
                    Interlocked.Increment(ref _deviceAccessCount);
                return device;
            }
        }
    }
}
