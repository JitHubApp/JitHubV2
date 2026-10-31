using System.Runtime.CompilerServices;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class DetachedFallbackDurableRetirementTests
{
    [Fact]
    public void CanceledUnadoptedFallbackRemainsRootedUntilNativeDisposeRetrySucceeds()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new LayoutDisposalProbe(failCanvasTextLayout: true);
        using var canceled = new CancellationTokenSource();
        WeakReference<CanvasTextLayout>? candidateReference = null;
        (VectorSceneBox box, LayoutSnapshot snapshot, WeakReference<MarkdownVectorScene> scene) =
            CreateDetachedMeasuredBox();

        try
        {
            box.ReleaseSceneGraph();
            AggregateException? failure = Assert.Throws<AggregateException>(() =>
                box.PrepareDetachedFallback(
                    canceled.Token,
                    (target, token) =>
                    {
                        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(
                            MarkdownDetachedSceneFallback.Create(target, token));
                        candidateReference = new WeakReference<CanvasTextLayout>(layout);
                        canceled.Cancel();
                        return layout;
                    },
                    replaceExisting: true,
                    nativeRetirement: retirement,
                    nativeDisposer: disposer.Dispose));

            Assert.Contains(failure.Flatten().InnerExceptions, exception => exception is OperationCanceledException);
            Assert.Contains(failure.Flatten().InnerExceptions, exception => exception is InvalidOperationException);
            Assert.False(box.HasDetachedFallback);
            Assert.Equal(1, retirement.PendingRecordCount);
            Assert.Equal(1, retirement.PendingResourceCount);
            Assert.Single(disposer.Layouts);
            failure = null;
        }
        finally
        {
            snapshot.Dispose();
        }

        CollectGarbage();
        Assert.False(IsAlive(scene), "Canceled candidate custody must not retain the scene graph.");
        Assert.True(IsAlive(candidateReference!), "The failed native slot was not durably rooted.");
        Assert.False(retirement.DrainOnce());
        Assert.Equal(1, retirement.PendingResourceCount);

        disposer.ClearFault();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        CollectGarbage();
        Assert.False(IsAlive(candidateReference!));
    }

    [Fact]
    public void TerminalBoxDisposalPublishesAdoptedFallbackBeforeReleasingOwner()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new LayoutDisposalProbe(failCanvasTextLayout: true);
        (WeakReference<VectorSceneBox> boxReference, WeakReference<MarkdownVectorScene> scene) =
            CreateAndRetireAdoptedFallback(retirement, disposer);

        CollectGarbage();
        Assert.False(IsAlive(boxReference), "Terminal native custody must not retain the former box.");
        Assert.False(IsAlive(scene), "Terminal native custody must not retain the vector scene.");
        Assert.True(IsAlive(disposer.Layouts[0]), "The failed layout slot must remain alive in custody.");

        Assert.False(retirement.DrainOnce());
        Assert.Equal(1, retirement.PendingResourceCount);
        disposer.ClearFault();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        CollectGarbage();
        Assert.False(IsAlive(disposer.Layouts[0]));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<VectorSceneBox> Box, WeakReference<MarkdownVectorScene> Scene)
        CreateAndRetireAdoptedFallback(NativeResourceRetirement retirement, LayoutDisposalProbe disposer)
    {
        (VectorSceneBox box, LayoutSnapshot snapshot, WeakReference<MarkdownVectorScene> scene) =
            CreateDetachedMeasuredBox();
        var boxReference = new WeakReference<VectorSceneBox>(box);
        try
        {
            box.ReleaseSceneGraph();
            Assert.True(box.PrepareDetachedFallback(
                CancellationToken.None,
                MarkdownDetachedSceneFallback.Create,
                nativeRetirement: retirement,
                nativeDisposer: disposer.Dispose));
            Assert.True(box.HasDetachedFallback);
            Assert.Empty(disposer.Layouts);
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(box.Dispose);
            Assert.Contains("Injected CanvasTextLayout disposal failure", failure.Message);
            Assert.True(box.IsDisposed);
            Assert.False(box.HasDetachedFallback);
            Assert.Equal(1, retirement.PendingRecordCount);
            Assert.Equal(1, retirement.PendingResourceCount);
            Assert.Single(disposer.Layouts);
        }
        finally
        {
            snapshot.Dispose();
        }
        return (boxReference, scene);
    }

    // WeakReference.TryGetTarget produces a strong local. Keep that local out
    // of the test method's subsequent GC window so the test itself is not an owner.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> reference) where T : class =>
        reference.TryGetTarget(out _);

    [Fact]
    public void SameBoxFallbackAliasIsNotAdoptedOrDoubleDisposed()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var created = CreateDetachedMeasuredBox();
        VectorSceneBox box = created.Box;
        LayoutSnapshot snapshot = created.Snapshot;
        try
        {
            box.ReleaseSceneGraph();
            Assert.True(box.PrepareDetachedFallback(CancellationToken.None, MarkdownDetachedSceneFallback.Create));
            var field = typeof(VectorSceneBox).GetField(
                "_detachedFallbackLayout",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            CanvasTextLayout existing = Assert.IsType<CanvasTextLayout>(field!.GetValue(box));

            Assert.False(box.PrepareDetachedFallback(
                CancellationToken.None,
                (_, _) => existing,
                replaceExisting: true,
                nativeRetirement: retirement));

            Assert.True(box.HasDetachedFallback);
            Assert.Equal(0, retirement.PendingRecordCount);
            Assert.Equal(0, retirement.PendingResourceCount);
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (VectorSceneBox Box, LayoutSnapshot Snapshot, WeakReference<MarkdownVectorScene> Scene)
        CreateDetachedMeasuredBox()
    {
        const string source = "detached fallback text used only to validate native ownership";
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        var style = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.Diagram] = style,
            [MarkdownElementKeys.Link] = style,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap(source),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
        MarkdownVectorPathOperation[] path =
        [
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(0, 0)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(20, 0)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(20, 20)),
            MarkdownVectorPathOperation.Close(),
        ];
        var scene = new MarkdownVectorScene(
            width: 96,
            height: 48,
            baseline: 40,
            commands: [MarkdownVectorCommand.FillPath(path)],
            referenceFontSize: 16,
            viewport: new MarkdownVectorRectangle(0, 0, 96, 48),
            semantics: Array.Empty<MarkdownVectorSemanticItem>(),
            links: Array.Empty<MarkdownVectorLinkAction>());
        var contentBuilder = new MarkdownContentBuilder();
        contentBuilder.AddVectorScene(
            scene,
            new SourceSpan(0, source.Length),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "detached test scene",
            semanticText: source);
        var box = new VectorSceneBox(
            context,
            Assert.Single(contentBuilder.Build().Items),
            MarkdownElementKeys.Diagram)
        {
            BlockIndex = 0,
        };
        float height = box.Measure(160);
        box.Arrange(4, 8, 160);
        var snapshot = new LayoutSnapshot([box], context.SourceMap, width: 160, height: height);
        return (box, snapshot, new WeakReference<MarkdownVectorScene>(scene));
    }

    private static void CollectGarbage()
    {
        for (int iteration = 0; iteration < 4; iteration++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private sealed class LayoutDisposalProbe(bool failCanvasTextLayout)
    {
        private bool _failCanvasTextLayout = failCanvasTextLayout;
        private readonly List<WeakReference<CanvasTextLayout>> _layouts = [];

        internal IReadOnlyList<WeakReference<CanvasTextLayout>> Layouts => _layouts;

        internal void ClearFault() => _failCanvasTextLayout = false;

        internal void Dispose(IDisposable resource)
        {
            if (resource is CanvasTextLayout layout)
            {
                if (_layouts.Count == 0 || !_layouts[^1].TryGetTarget(out CanvasTextLayout? previous) ||
                    !ReferenceEquals(previous, layout))
                    _layouts.Add(new WeakReference<CanvasTextLayout>(layout));
                if (_failCanvasTextLayout)
                    throw new InvalidOperationException("Injected CanvasTextLayout disposal failure.");
            }

            resource.Dispose();
        }
    }
}
