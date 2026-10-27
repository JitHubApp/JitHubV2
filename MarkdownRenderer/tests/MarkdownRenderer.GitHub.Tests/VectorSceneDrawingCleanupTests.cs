using System.Collections.Generic;
using System.Reflection;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class VectorSceneDrawingCleanupTests
{
    [Fact]
    public void DrawingCleanupAttemptsEveryResourceAndBoundsFailureDetails()
    {
        const int resourceCount = 100;
        var resources = new ProbeResource[resourceCount];
        BlockBoxCleanupScope cleanup = default;
        for (int index = 0; index < resources.Length; index++)
        {
            resources[index] = new ProbeResource(
                new InvalidOperationException($"resource cleanup {index}"));
            cleanup.TryDispose(resources[index]);
        }

        AggregateException failure = Assert.Throws<AggregateException>(
            () => cleanup.ThrowIfAny("disposing vector drawing resources"));

        Assert.All(resources, resource => Assert.Equal(1, resource.DisposeCount));
        Assert.Equal(9, failure.InnerExceptions.Count);
        Assert.Contains("omitted", failure.InnerExceptions[^1].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DrawingCleanupPreservesTheSingleOriginalException()
    {
        var expected = new InvalidOperationException("native disposal failed");
        BlockBoxCleanupScope cleanup = default;
        cleanup.TryDispose(new ProbeResource(expected));

        Exception actual = Assert.Throws<InvalidOperationException>(
            () => cleanup.ThrowIfAny("disposing vector drawing resources"));

        Assert.Same(expected, actual);
    }

    [Fact]
    public void DrawingCleanupClearsOnlySuccessfulSlotsAndRetriesFailedOnceResources()
    {
        var retryableFailure = new InvalidOperationException("transient native disposal failure");
        var persistentFailure = new InvalidOperationException("persistent native disposal failure");
        var retryableResource = new ProbeResource(retryableFailure, failuresBeforeSuccess: 1);
        ProbeResource? retryable = retryableResource;
        ProbeResource? persistent = new(persistentFailure);
        ProbeResource? successful = new(new InvalidOperationException("not thrown"), failuresBeforeSuccess: 0);

        BlockBoxCleanupScope firstAttempt = default;
        firstAttempt.TryDisposeAndClear(ref retryable);
        firstAttempt.TryDisposeAndClear(ref persistent);
        firstAttempt.TryDisposeAndClear(ref successful);

        AggregateException firstFailure = Assert.Throws<AggregateException>(
            () => firstAttempt.ThrowIfAny("disposing vector drawing resources"));

        Assert.Equal(1, retryable!.DisposeCount);
        Assert.Equal(1, persistent!.DisposeCount);
        Assert.Null(successful);
        Assert.Same(retryableFailure, firstFailure.InnerExceptions[0]);
        Assert.Same(persistentFailure, firstFailure.InnerExceptions[1]);

        BlockBoxCleanupScope retry = default;
        retry.TryDisposeAndClear(ref retryable);
        retry.TryDisposeAndClear(ref persistent);

        InvalidOperationException secondFailure = Assert.Throws<InvalidOperationException>(
            () => retry.ThrowIfAny("retrying vector drawing cleanup"));

        Assert.Same(persistentFailure, secondFailure);
        Assert.Null(retryable);
        Assert.NotNull(persistent);
        Assert.Equal(2, retryableResource.DisposeCount);
        Assert.Equal(2, persistent!.DisposeCount);
    }

    [Fact]
    public void DrawingCleanupAttemptsEveryByRefSlotAndBoundsRetainedFailureDetails()
    {
        const int resourceCount = 100;
        var resources = new ProbeResource?[resourceCount];
        for (int index = 0; index < resources.Length; index++)
            resources[index] = new ProbeResource(new InvalidOperationException($"persistent cleanup {index}"));

        BlockBoxCleanupScope cleanup = default;
        for (int index = 0; index < resources.Length; index++)
            cleanup.TryDisposeAndClear(ref resources[index]);

        AggregateException failure = Assert.Throws<AggregateException>(
            () => cleanup.ThrowIfAny("disposing vector drawing resources"));

        Assert.DoesNotContain(resources, resource => resource is null);
        Assert.All(resources, resource =>
            Assert.Equal(1, Assert.IsType<ProbeResource>(resource).DisposeCount));
        Assert.Equal(9, failure.InnerExceptions.Count);
        Assert.Contains("omitted", failure.InnerExceptions[^1].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConstructionScopeCanRetryPartialVectorPreparationAndRetireTheOwner()
    {
        var injectedFailure = new InvalidOperationException("second path device request failed");
        var resourceCreator = new FailOnceResourceCreator(
            CanvasDevice.GetSharedDevice(),
            failAtRequest: 2,
            injectedFailure);
        MarkdownLayoutContext context = CreateContext(resourceCreator);
        using MarkdownLayoutConstructionScope construction = context.BeginConstructionScope();
        VectorSceneBox box = CreateTwoPathVectorBox(context);
        context.RegisterConstructionBlock(box);

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => box.Measure(320));

        Assert.Same(injectedFailure, actual);
        Assert.Equal(2, resourceCreator.DeviceRequestCount);
        Assert.False(box.IsNativeResourcesPrepared);

        // The failed attempt created and then cleaned the first real Win2D
        // geometry. A later attempt can reuse the same owner and prepare a
        // complete resource set; construction-scope retirement then owns it.
        _ = box.Measure(320);
        Assert.True(box.IsNativeResourcesPrepared);

        construction.Dispose();
        Assert.True(box.IsDisposed);
        Assert.False(box.IsNativeResourcesPrepared);
    }

    [Fact]
    public void EagerInlineConstructorRegistersBeforePreparationAndScopeRetiresPartialRun()
    {
        var injectedFailure = new InvalidOperationException("second inline path device request failed");
        var resourceCreator = new FailOnceResourceCreator(
            CanvasDevice.GetSharedDevice(),
            failAtRequest: 2,
            injectedFailure);
        MarkdownLayoutContext context = CreateContext(resourceCreator);
        using MarkdownLayoutConstructionScope construction = context.BeginConstructionScope();

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() =>
            _ = new InlineVectorSceneRun(
                context,
                CreateTwoPathScene(),
                semanticText: "formula",
                accessibilityName: "Formula",
                accessibilityDescription: "Cleanup ownership test.",
                elementKey: MarkdownElementKeys.Math));

        Assert.Same(injectedFailure, actual);
        Assert.Equal(2, resourceCreator.DeviceRequestCount);

        FieldInfo rootsField = Assert.IsAssignableFrom<FieldInfo>(
            typeof(MarkdownLayoutConstructionScope).GetField("_roots", BindingFlags.Instance | BindingFlags.NonPublic));
        var roots = Assert.IsType<HashSet<object>>(rootsField.GetValue(construction));
        InlineVectorSceneRun run = Assert.IsType<InlineVectorSceneRun>(
            Assert.Single(roots, static root => root is InlineVectorSceneRun));
        Assert.False(run.IsNativeResourcesPrepared);

        construction.Dispose();

        Assert.False(run.IsNativeResourcesPrepared);
        Assert.Throws<ObjectDisposedException>(() => _ = run.Scene);
        Assert.Throws<ObjectDisposedException>(() => run.Measure(16));
    }

    [Fact]
    public void FailedDrawTextPreparationOwnsAndClearsPendingFormatBeforeRetry()
    {
        var injectedFailure = new InvalidOperationException("text layout device request failed");
        FieldInfo pendingFormatField = Assert.IsAssignableFrom<FieldInfo>(
            typeof(VectorSceneDrawing).GetField("_pendingTextFormat", BindingFlags.Instance | BindingFlags.NonPublic));
        VectorSceneDrawing? drawing = null;
        CanvasTextFormat? pendingFormatAtFailure = null;
        var resourceCreator = new FailOnceResourceCreator(
            CanvasDevice.GetSharedDevice(),
            failAtRequest: 2,
            injectedFailure,
            beforeFailure: () =>
                pendingFormatAtFailure = drawing is null
                    ? null
                    : pendingFormatField.GetValue(drawing) as CanvasTextFormat);
        MarkdownLayoutContext context = CreateContext(resourceCreator);
        using MarkdownLayoutConstructionScope construction = context.BeginConstructionScope();
        drawing = new VectorSceneDrawing(
            resourceCreator,
            CreatePathAndTextScene(),
            CancellationToken.None,
            deferNativeResources: true);
        context.RegisterConstructionResource(drawing);

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => drawing.PrepareNativeResources(CancellationToken.None));

        Assert.Same(injectedFailure, actual);
        Assert.Equal(2, resourceCreator.DeviceRequestCount);
        Assert.NotNull(pendingFormatAtFailure);
        Assert.Null(pendingFormatField.GetValue(drawing));
        Assert.False(drawing.IsPrepared);

        drawing.PrepareNativeResources(CancellationToken.None);
        Assert.True(drawing.IsPrepared);

        construction.Dispose();
        Assert.False(drawing.IsPrepared);
        Assert.Throws<ObjectDisposedException>(() => _ = drawing.Scene);
    }

    private sealed class ProbeResource : IDisposable
    {
        private readonly Exception _failure;
        private int _remainingFailures;

        public ProbeResource(Exception failure, int failuresBeforeSuccess = int.MaxValue)
        {
            _failure = failure;
            _remainingFailures = failuresBeforeSuccess;
        }

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (_remainingFailures <= 0)
                return;

            _remainingFailures--;
            throw _failure;
        }
    }

    private static MarkdownLayoutContext CreateContext(ICanvasResourceCreator resourceCreator)
    {
        var style = new ElementStyle
        {
            FontSize = 16,
            Foreground = Color.FromArgb(0xFF, 0x20, 0x20, 0x20),
            Background = Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA),
        };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.Diagram] = style,
            [MarkdownElementKeys.Math] = style,
            [MarkdownElementKeys.Link] = style,
        };
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
            surfaceColor: style.Background.Value,
            selectionHighlightColor: Color.FromArgb(0xFF, 0x00, 0x66, 0xCC),
            selectionForegroundColor: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            focusVisualColor: style.Foreground,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            resourceCreator,
            theme,
            new MarkdownSourceMap("diagram"),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }

    private static VectorSceneBox CreateTwoPathVectorBox(MarkdownLayoutContext context)
    {
        var content = new MarkdownContentBuilder();
        content.AddVectorScene(
            CreateTwoPathScene(),
            new SourceSpan(0, 7),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "Cleanup test diagram");
        return new VectorSceneBox(
            context,
            Assert.Single(content.Build().Items),
            MarkdownElementKeys.Diagram)
        {
            BlockIndex = 0,
        };
    }

    private static MarkdownVectorScene CreateTwoPathScene()
    {
        static IReadOnlyList<MarkdownVectorPathOperation> Path(float offset) =>
        [
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(offset, 0)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(offset + 10, 10)),
            MarkdownVectorPathOperation.Close(),
        ];

        var scene = new MarkdownVectorScene(
            width: 40,
            height: 40,
            baseline: 40,
            commands:
            [
                MarkdownVectorCommand.FillPath(Path(0)),
                MarkdownVectorCommand.FillPath(Path(20)),
            ]);
        return scene;
    }

    private static MarkdownVectorScene CreatePathAndTextScene()
    {
        static IReadOnlyList<MarkdownVectorPathOperation> Path() =>
        [
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(0, 0)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(10, 10)),
            MarkdownVectorPathOperation.Close(),
        ];

        return new MarkdownVectorScene(
            width: 40,
            height: 40,
            baseline: 40,
            commands:
            [
                MarkdownVectorCommand.FillPath(Path()),
                MarkdownVectorCommand.DrawText(
                    "label",
                    new MarkdownVectorPoint(4, 24),
                    new MarkdownVectorTextStyle("Arial", 12),
                    new MarkdownVectorPaintStyle(fillArgb: 0xFF202020)),
            ]);
    }

    private sealed class FailOnceResourceCreator(
        CanvasDevice device,
        int failAtRequest,
        Exception failure,
        Action? beforeFailure = null) : ICanvasResourceCreator
    {
        private int _deviceRequestCount;
        private int _failed;

        internal int DeviceRequestCount => Volatile.Read(ref _deviceRequestCount);

        public CanvasDevice Device
        {
            get
            {
                int request = Interlocked.Increment(ref _deviceRequestCount);
                if (request == failAtRequest && Interlocked.Exchange(ref _failed, 1) == 0)
                {
                    beforeFailure?.Invoke();
                    throw failure;
                }
                return device;
            }
        }
    }
}
