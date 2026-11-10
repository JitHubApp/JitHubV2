using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Exercises actual Win2D inline-layout custody. The injected cleanup probe
/// stores weak references only; the timerless registry is the sole native
/// owner after a terminal handoff.
/// </summary>
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class InlineNativeDurableRetirementTests
{
    private const float Width = 320f;
    private const string Text = "native retirement keeps source ownership local";

    [Fact]
    public void PublicContextAndElementKeyConstructorSignatureRemainsAvailable()
    {
        ConstructorInfo? constructor = typeof(InlineContainerBox).GetConstructor(
            [typeof(MarkdownLayoutContext), typeof(string)]);

        Assert.NotNull(constructor);
        Assert.True(constructor!.IsPublic);
    }

    [Fact]
    public void CancelledMeasureRestoresPublishedLayoutAndTransfersFailedCandidate()
    {
        using var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        var context = harness.CreateContext(cancellation.Token);
        var disposer = new LayoutDisposalProbe();
        var retirement = new NativeResourceRetirement(createTimer: false, disposer.Dispose);
        InlineContainerBox box = CreateTextRoot(context, retirement);
        BuildSelection(box, harness.Device, 0, 12);

        CanvasTextLayout originalLayout = Required(box.TextLayout);
        NativeResourceRetirementRecord originalRecord = Required(box.TextLayoutRetirementRecord);
        CanvasTextLayout originalSelection = Required(box.SelectionLayout);
        NativeResourceRetirementRecord originalSelectionRecord = Required(box.SelectionLayoutRetirementRecord);
        Rect originalBounds = box.Bounds;

        disposer.FailNextDisposal();
        harness.Creator.CancelOnNextDeviceAccess = cancellation.Cancel;

        OperationCanceledException failure = Assert.ThrowsAny<OperationCanceledException>(() => box.Measure(Width - 73f));
        Assert.Same(originalLayout, Required(box.TextLayout));
        Assert.Same(originalRecord, Required(box.TextLayoutRetirementRecord));
        Assert.Same(originalSelection, Required(box.SelectionLayout));
        Assert.Same(originalSelectionRecord, Required(box.SelectionLayoutRetirementRecord));
        Assert.Equal(originalBounds, box.Bounds);
        Assert.NotNull(failure.Data["MarkdownRenderer.InlineContainerBox.MeasureCleanupFailure"]);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);

        WeakReference<CanvasTextLayout> failedCandidate = Assert.Single(disposer.ObservedLayouts);
        disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        CollectGarbage();
        AssertCollected(failedCandidate);
        box.Dispose();
        GC.KeepAlive(retirement);
    }

    [Fact]
    public void SuccessfulWidthReflowPairsNewLayoutWithItsRecordAndRetiresBothOldLayouts()
    {
        using var harness = new Harness();
        var context = harness.CreateContext();
        var disposer = new LayoutDisposalProbe();
        var retirement = new NativeResourceRetirement(createTimer: false, disposer.Dispose);
        InlineContainerBox box = CreateTextRoot(context, retirement);
        BuildSelection(box, harness.Device, 0, 12);

        CanvasTextLayout oldLayout = Required(box.TextLayout);
        NativeResourceRetirementRecord oldLayoutRecord = Required(box.TextLayoutRetirementRecord);
        CanvasTextLayout oldSelection = Required(box.SelectionLayout);
        NativeResourceRetirementRecord oldSelectionRecord = Required(box.SelectionLayoutRetirementRecord);
        disposer.FailFor(oldLayout, oldSelection);

        _ = box.Measure(Width - 89f);

        CanvasTextLayout newLayout = Required(box.TextLayout);
        NativeResourceRetirementRecord newLayoutRecord = Required(box.TextLayoutRetirementRecord);
        Assert.NotSame(oldLayout, newLayout);
        Assert.NotSame(oldLayoutRecord, newLayoutRecord);
        Assert.Null(box.SelectionLayout);
        Assert.Null(box.SelectionLayoutRetirementRecord);
        Assert.Equal(2, retirement.PendingRecordCount);
        Assert.Equal(2, retirement.PendingResourceCount);
        Assert.Equal(2, disposer.FailedDisposalCount);

        disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        box.Dispose();
        GC.KeepAlive(retirement);
    }

    [Fact]
    public void TerminalDisposeLetsBoxContextAndRunsCollectWhileRegistryRetainsFailedLayouts()
    {
        using var harness = new Harness();
        var disposer = new LayoutDisposalProbe();
        var retirement = new NativeResourceRetirement(createTimer: false, disposer.Dispose);
        TerminalEvidence evidence = CreateTerminalEvidence(harness, retirement, disposer);

        Assert.Equal(2, retirement.PendingRecordCount);
        Assert.Equal(2, retirement.PendingResourceCount);
        CollectGarbage();
        AssertCollected(evidence.Box);
        AssertCollected(evidence.Context);
        Assert.All(evidence.Runs, reference => AssertCollected(reference));
        Assert.All(evidence.Layouts, reference => AssertRetained(reference));
        GC.KeepAlive(retirement);

        evidence.Disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        CollectGarbage();
        Assert.All(evidence.Layouts, AssertCollected);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public void IntrinsicTemporaryLayoutFailureRestoresAtomicRunAndKeepsPublishedLayout()
    {
        using var harness = new Harness();
        var context = harness.CreateContext();
        context.UseProgressiveNativeSceneResidency = true;
        var disposer = new LayoutDisposalProbe();
        var retirement = new NativeResourceRetirement(createTimer: false, disposer.Dispose);
        InlineContainerBox box = CreateTextRoot(context, retirement, addText: false);
        var sceneRun = new InlineVectorSceneRun(
            context,
            new MarkdownVectorScene(24, 12, 9, []),
            "formula",
            "formula",
            null,
            MarkdownElementKeys.Math)
        {
            SourceSpan = new SourceSpan(0, 7),
        };
        box.Add(sceneRun);
        _ = box.Measure(Width);
        box.Arrange(0, 0, Width);

        // Give the atomic run a distinct pre-query state so rollback must restore
        // all scalar endpoints rather than merely reproduce its normal measure.
        sceneRun.Measure(48f);
        sceneRun.SetLocalBounds(new Rect(9, 11, 37, 19));
        float oldDesiredWidth = sceneRun.DesiredWidth;
        float oldDesiredHeight = sceneRun.DesiredHeight;
        float oldBaseline = sceneRun.DesiredBaseline;
        Rect oldLocalBounds = sceneRun.LocalBounds;
        CanvasTextLayout publishedLayout = Required(box.TextLayout);
        NativeResourceRetirementRecord publishedRecord = Required(box.TextLayoutRetirementRecord);

        disposer.FailNextDisposal();
        InvalidOperationException cleanupFailure = Assert.Throws<InvalidOperationException>(
            () => box.MeasureIntrinsicWidths());

        Assert.Equal("Injected native text-layout disposal failure.", cleanupFailure.Message);
        Assert.Same(publishedLayout, Required(box.TextLayout));
        Assert.Same(publishedRecord, Required(box.TextLayoutRetirementRecord));
        Assert.Equal(oldDesiredWidth, sceneRun.DesiredWidth);
        Assert.Equal(oldDesiredHeight, sceneRun.DesiredHeight);
        Assert.Equal(oldBaseline, sceneRun.DesiredBaseline);
        Assert.Equal(oldLocalBounds, sceneRun.LocalBounds);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);

        disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        box.Dispose();
        GC.KeepAlive(retirement);
    }

    [Fact]
    public void SelectionCandidateFailureLeavesPriorSelectionAndItsRetirementRecordCached()
    {
        using var harness = new Harness();
        var context = harness.CreateContext();
        var disposer = new LayoutDisposalProbe();
        var retirement = new NativeResourceRetirement(createTimer: false, disposer.Dispose);
        InlineContainerBox box = CreateTextRoot(context, retirement);
        Color originalColor = Color.FromArgb(0xFF, 0x24, 0x68, 0xA0);
        Color replacementColor = Color.FromArgb(0xFF, 0xA0, 0x42, 0x18);
        BuildSelection(box, harness.Device, 0, 9, originalColor);
        CanvasTextLayout oldSelection = Required(box.SelectionLayout);
        NativeResourceRetirementRecord oldRecord = Required(box.SelectionLayoutRetirementRecord);

        harness.Creator.ThrowOnNextDeviceAccess = new InvalidOperationException("Injected selection candidate factory failure.");
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => BuildSelection(box, harness.Device, 10, 8, replacementColor));

        Assert.Equal("Injected selection candidate factory failure.", failure.Message);
        Assert.Same(oldSelection, Required(box.SelectionLayout));
        Assert.Same(oldRecord, Required(box.SelectionLayoutRetirementRecord));
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        box.Dispose();
        GC.KeepAlive(retirement);
    }

    [Fact]
    public void PlacementTransferSwapsLayoutRecordsTogetherAndRetiresAtTheNewOwner()
    {
        using var harness = new Harness();
        var disposer = new LayoutDisposalProbe();
        var retirement = new NativeResourceRetirement(createTimer: false, disposer.Dispose);
        PlacementEvidence evidence = CreateAndRetireTransferredLayouts(harness, retirement, disposer);

        Assert.Equal(2, retirement.PendingRecordCount);
        Assert.Equal(2, retirement.PendingResourceCount);
        CollectGarbage();
        AssertCollected(evidence.SourceBox);
        AssertCollected(evidence.PreparedBox);
        AssertCollected(evidence.SourceContext);
        AssertCollected(evidence.PreparedContext);
        Assert.All(evidence.SourceRuns, reference => AssertCollected(reference));
        Assert.All(evidence.PreparedRuns, reference => AssertCollected(reference));
        AssertRetained(evidence.SourcePrimary);
        AssertRetained(evidence.SourceSelection);
        AssertCollected(evidence.PreparedPrimary);
        GC.KeepAlive(retirement);

        evidence.Disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        CollectGarbage();
        AssertCollected(evidence.SourcePrimary);
        AssertCollected(evidence.SourceSelection);
        GC.KeepAlive(retirement);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TerminalEvidence CreateTerminalEvidence(
        Harness harness,
        NativeResourceRetirement retirement,
        LayoutDisposalProbe disposer)
    {
        MarkdownLayoutContext context = harness.CreateContext();
        InlineContainerBox box = CreateTextRoot(context, retirement);
        TextRun run = Assert.IsType<TextRun>(Assert.Single(box.Runs));
        BuildSelection(box, harness.Device, 0, 12);
        CanvasTextLayout primary = Required(box.TextLayout);
        CanvasTextLayout selection = Required(box.SelectionLayout);
        disposer.FailFor(primary, selection);

        var evidence = new TerminalEvidence(
            disposer,
            new WeakReference<InlineContainerBox>(box),
            new WeakReference<MarkdownLayoutContext>(context),
            [new WeakReference<InlineRun>(run)],
            [new WeakReference<CanvasTextLayout>(primary), new WeakReference<CanvasTextLayout>(selection)]);
        Assert.ThrowsAny<Exception>(box.Dispose);
        return evidence;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PlacementEvidence CreateAndRetireTransferredLayouts(
        Harness harness,
        NativeResourceRetirement retirement,
        LayoutDisposalProbe disposer)
    {
        MarkdownLayoutContext sourceContext = harness.CreateContext();
        MarkdownLayoutContext preparedContext = harness.CreateContext();
        InlineContainerBox source = CreateTextRoot(sourceContext, retirement);
        BuildSelection(source, harness.Device, 0, 12);
        Assert.True(source.TryCreatePrivateInlinePlacementClone(
            preparedContext,
            Width,
            out InlineContainerBox? prepared,
            out _));
        InlineContainerBox candidate = Assert.IsType<InlineContainerBox>(prepared);
        Assert.True(InlineMeasuredPlacementCapture.TryCaptureContainerLeafMeasuredPlacementTransfer(source, candidate, Width, out var transfer));
        Assert.NotNull(transfer);

        // Capture is intentionally before candidate Measure/Arrange: the
        // transfer fences an unmeasured private clone, then verifies the
        // measured native replacement at the commit boundary.
        _ = candidate.Measure(Width);
        candidate.Arrange(0, 0, Width);

        CanvasTextLayout sourcePrimary = Required(source.TextLayout);
        NativeResourceRetirementRecord sourcePrimaryRecord = Required(source.TextLayoutRetirementRecord);
        CanvasTextLayout sourceSelection = Required(source.SelectionLayout);
        NativeResourceRetirementRecord sourceSelectionRecord = Required(source.SelectionLayoutRetirementRecord);
        CanvasTextLayout preparedPrimary = Required(candidate.TextLayout);
        NativeResourceRetirementRecord preparedPrimaryRecord = Required(candidate.TextLayoutRetirementRecord);

        Assert.True(transfer!.IsCurrentAndPrepared());
        transfer.SwapPlacementNoThrow();

        Assert.Same(preparedPrimary, Required(source.TextLayout));
        Assert.Same(preparedPrimaryRecord, Required(source.TextLayoutRetirementRecord));
        Assert.Same(sourcePrimary, Required(candidate.TextLayout));
        Assert.Same(sourcePrimaryRecord, Required(candidate.TextLayoutRetirementRecord));
        Assert.Same(sourceSelection, Required(candidate.SelectionLayout));
        Assert.Same(sourceSelectionRecord, Required(candidate.SelectionLayoutRetirementRecord));
        Assert.Null(source.SelectionLayout);
        Assert.Null(source.SelectionLayoutRetirementRecord);

        disposer.FailFor(sourcePrimary, sourceSelection);
        Assert.ThrowsAny<Exception>(candidate.Dispose);
        source.Dispose();

        return new PlacementEvidence(
            disposer,
            new WeakReference<InlineContainerBox>(source),
            new WeakReference<InlineContainerBox>(candidate),
            new WeakReference<MarkdownLayoutContext>(sourceContext),
            new WeakReference<MarkdownLayoutContext>(preparedContext),
            source.Runs.Select(static run => new WeakReference<InlineRun>(run)).ToArray(),
            candidate.Runs.Select(static run => new WeakReference<InlineRun>(run)).ToArray(),
            new WeakReference<CanvasTextLayout>(sourcePrimary),
            new WeakReference<CanvasTextLayout>(sourceSelection),
            new WeakReference<CanvasTextLayout>(preparedPrimary));
    }

    private static InlineContainerBox CreateTextRoot(
        MarkdownLayoutContext context,
        NativeResourceRetirement retirement,
        bool addText = true)
    {
        var root = new InlineContainerBox(context, MarkdownElementKeys.Body, retirement)
        {
            BlockIndex = context.NextBlockIndex(),
            UseContainerMargin = false,
            UseContainerPadding = false,
        };
        if (addText)
        {
            root.Add(new TextRun(Text) { SourceSpan = new SourceSpan(0, Text.Length) });
            _ = root.Measure(Width);
            root.Arrange(0, 0, Width);
        }
        return root;
    }

    private static void BuildSelection(
        InlineContainerBox box,
        CanvasDevice device,
        int start,
        int length,
        Color? color = null)
    {
        using var target = new CanvasRenderTarget(device, Width, System.Math.Max(64, (float)box.Bounds.Height), 96);
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        drawing.Clear(Color.FromArgb(0, 0, 0, 0));
        box.PaintSelectionForeground(
            drawing,
            new DocumentRange(
                new DocumentPosition(box.BlockIndex, 0, start),
                new DocumentPosition(box.BlockIndex, 0, start + length)),
            color ?? box.Context.ThemeSnapshot.SelectionForegroundColor,
            new Rect(0, 0, Width, System.Math.Max(64, box.Bounds.Height)));
    }

    private static T Required<T>(T? value, [CallerArgumentExpression(nameof(value))] string? name = null)
        where T : class =>
        value ?? throw new InvalidOperationException($"Expected {name} to contain a {typeof(T).Name}.");

    private static void AssertCollected<T>(WeakReference<T> reference)
        where T : class => Assert.False(IsAlive(reference), "A retired inline object remained strongly rooted.");

    private static void AssertRetained<T>(WeakReference<T> reference)
        where T : class => Assert.True(IsAlive(reference), "The durable retirement registry lost a failed native layout.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> reference)
        where T : class => reference.TryGetTarget(out _);

    private static void CollectGarbage()
    {
        for (int iteration = 0; iteration < 3; iteration++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private sealed record TerminalEvidence(
        LayoutDisposalProbe Disposer,
        WeakReference<InlineContainerBox> Box,
        WeakReference<MarkdownLayoutContext> Context,
        WeakReference<InlineRun>[] Runs,
        WeakReference<CanvasTextLayout>[] Layouts);

    private sealed record PlacementEvidence(
        LayoutDisposalProbe Disposer,
        WeakReference<InlineContainerBox> SourceBox,
        WeakReference<InlineContainerBox> PreparedBox,
        WeakReference<MarkdownLayoutContext> SourceContext,
        WeakReference<MarkdownLayoutContext> PreparedContext,
        WeakReference<InlineRun>[] SourceRuns,
        WeakReference<InlineRun>[] PreparedRuns,
        WeakReference<CanvasTextLayout> SourcePrimary,
        WeakReference<CanvasTextLayout> SourceSelection,
        WeakReference<CanvasTextLayout> PreparedPrimary);

    private sealed class Harness : IDisposable
    {
        private readonly MarkdownRenderResources _renderResources = new();

        private readonly MarkdownSourceMap _sourceMap = new(Text);
        private readonly MarkdownExtensionRegistry _registry = new();
        private readonly IReadOnlyDictionary<string, bool> _disclosureStates =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly ThemeSnapshot _theme;

        internal Harness()
        {
            Device = new CanvasDevice(forceSoftwareRenderer: true);
            Creator = new ControlledResourceCreator(Device);
            var body = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
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
        }

        internal CanvasDevice Device { get; }
        internal ControlledResourceCreator Creator { get; }

        internal MarkdownLayoutContext CreateContext(CancellationToken cancellationToken = default) =>
            new(Creator, _theme, _sourceMap, _registry, FlowDirection.LeftToRight, language: "en-US")
            {
                RenderResources = _renderResources,
                CancellationToken = cancellationToken,
                DisclosureStates = _disclosureStates,
            };

        public void Dispose()
        {
            _renderResources.Images.BitmapCache.ReleaseDevice(Device);
            Device.Dispose();
        }
    }

    private sealed class ControlledResourceCreator(CanvasDevice device) : ICanvasResourceCreator
    {
        private Action? _cancelOnNextDeviceAccess;
        private Exception? _throwOnNextDeviceAccess;

        internal Action? CancelOnNextDeviceAccess
        {
            set => Interlocked.Exchange(ref _cancelOnNextDeviceAccess, value);
        }

        internal Exception? ThrowOnNextDeviceAccess
        {
            set => Interlocked.Exchange(ref _throwOnNextDeviceAccess, value);
        }

        public CanvasDevice Device
        {
            get
            {
                Exception? failure = Interlocked.Exchange(ref _throwOnNextDeviceAccess, null);
                if (failure is not null)
                    throw failure;
                Interlocked.Exchange(ref _cancelOnNextDeviceAccess, null)?.Invoke();
                return device;
            }
        }
    }

    private sealed class LayoutDisposalProbe
    {
        private readonly List<WeakReference<CanvasTextLayout>> _observed = [];
        private readonly List<WeakReference<CanvasTextLayout>> _failureTargets = [];
        private bool _failNext;
        private bool _allowDisposal;

        internal int FailedDisposalCount { get; private set; }
        internal WeakReference<CanvasTextLayout>[] ObservedLayouts => _observed.ToArray();

        internal void FailNextDisposal()
        {
            _failNext = true;
            _allowDisposal = false;
        }

        internal void FailFor(params CanvasTextLayout[] layouts)
        {
            _allowDisposal = false;
            foreach (CanvasTextLayout layout in layouts)
                _failureTargets.Add(new WeakReference<CanvasTextLayout>(layout));
        }

        internal void AllowDisposal() => _allowDisposal = true;

        internal void Dispose(IDisposable resource)
        {
            CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(resource);
            _observed.Add(new WeakReference<CanvasTextLayout>(layout));
            bool isTarget = false;
            foreach (WeakReference<CanvasTextLayout> target in _failureTargets)
            {
                if (target.TryGetTarget(out CanvasTextLayout? expected) && ReferenceEquals(expected, layout))
                {
                    isTarget = true;
                    break;
                }
            }
            if (!_allowDisposal && (_failNext || isTarget))
            {
                _failNext = false;
                FailedDisposalCount++;
                throw new InvalidOperationException("Injected native text-layout disposal failure.");
            }
            layout.Dispose();
        }
    }
}
