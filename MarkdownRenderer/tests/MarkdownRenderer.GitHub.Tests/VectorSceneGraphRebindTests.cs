using System.Runtime.CompilerServices;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class VectorSceneGraphRebindTests
{
    private const string Source = "before diagram source after";

    [Fact]
    public void DetachingDropsCommandGraphButPreservesSemanticAndLayoutProjections()
    {
        (VectorSceneBox box, LayoutSnapshot snapshot, WeakSceneGraph graph) = CreateMeasuredSemanticBox();
        try
        {
            Rect bounds = box.Bounds;
            Rect contentBounds = box.ContentBounds;
            Rect semanticBounds = box.GetSemanticBounds(0);
            var hitPoint = new Point(
                semanticBounds.X + semanticBounds.Width / 2,
                semanticBounds.Y + semanticBounds.Height / 2);
            Assert.True(box.HitTest(hitPoint, out DocumentPosition expectedHit));
            Assert.True(box.TryGetLinkAt(hitPoint, out MarkdownVectorLinkAction? expectedLink));
            FocusableItem expectedFocus = Assert.Single(snapshot.CollectFocusableItems());
            ElementStyle expectedTextStyle = box.ResolveSemanticTextStyle(0);
            MarkdownVectorSemanticItem expectedSemantic = Assert.Single(box.SemanticItems);
            MarkdownVectorLinkAction expectedSemanticLink = Assert.IsType<MarkdownVectorLinkAction>(
                box.GetSemanticLinkAction(0));

            box.ReleaseSceneGraph();

            Assert.True(box.IsGraphDetached);
            Assert.False(box.IsNativeResourcesPrepared);
            Assert.Throws<ObjectDisposedException>(() => _ = box.Scene);
            Assert.Equal(new SourceSpan(Source.IndexOf("diagram", StringComparison.Ordinal), 14), box.SourceRange);
            Assert.Equal(MarkdownAccessibilityRole.Diagram, box.AccessibilityRole);
            Assert.Equal("Search diagram", box.AccessibilityName);
            Assert.Equal("Search results by stage", box.AccessibilityDescription);
            Assert.Equal("diagram semantic copy", box.SemanticText);
            Assert.Same(expectedSemantic, Assert.Single(box.SemanticItems));
            Assert.Same(expectedSemanticLink, box.GetSemanticLinkAction(0));
            Assert.Equal(contentBounds, box.ContentBounds);
            Assert.Equal(semanticBounds, box.GetSemanticBounds(0));
            Assert.Same(expectedTextStyle, box.ResolveSemanticTextStyle(0));
            Assert.Equal(bounds, box.Bounds);
            Assert.True(box.HitTest(hitPoint, out DocumentPosition actualHit));
            Assert.Equal(expectedHit, actualHit);
            Assert.True(box.TryGetLinkAt(hitPoint, out MarkdownVectorLinkAction? actualLink));
            Assert.Same(expectedLink, actualLink);
            AssertFocusItem(expectedFocus, Assert.Single(snapshot.CollectFocusableItems()));

            // Reversible device-resource release must not trigger preparation
            // again merely because the scalar layout data remains available.
            box.ReleaseNativeResources();
            Assert.False(box.IsNativeResourcesPrepared);
            float measuredHeight = box.Measure(160);
            Assert.False(box.IsNativeResourcesPrepared);
            box.Arrange((float)bounds.X, (float)bounds.Y, (float)bounds.Width);
            Assert.Equal(bounds.Height, measuredHeight);
            Assert.Equal(bounds, box.Bounds);

            AssertSceneGraphCollected(graph, box, snapshot);
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    [Fact]
    public void ReplacementIsStagedWithoutMutationThenAdoptedWithoutChangingMetadata()
    {
        var (box, snapshot, graph) = CreateMeasuredSemanticBox();
        try
        {
            Rect bounds = box.Bounds;
            Rect contentBounds = box.ContentBounds;
            Rect semanticBounds = box.GetSemanticBounds(0);
            IReadOnlyList<MarkdownVectorSemanticItem> semantics = box.SemanticItems;
            ElementStyle semanticStyle = box.ResolveSemanticTextStyle(0);
            FocusableItem expectedFocus = Assert.Single(snapshot.CollectFocusableItems());
            VectorScenePreparation preparation = VectorScenePreparation.Capture(box);
            MarkdownVectorScene replacementScene = CreateScene();
            box.ReleaseSceneGraph();

            AssertSceneGraphCollected(graph, box, snapshot, preparation);

            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() =>
                    preparation.Create(replacementScene, canceled.Token));
            }
            Assert.True(box.IsGraphDetached);

            VectorSceneDrawing replacement = preparation.Create(
                replacementScene,
                CancellationToken.None);
            Assert.True(replacement.HasScene);
            Assert.True(replacement.IsPrepared);
            Assert.True(box.IsGraphDetached);
            Assert.Throws<ObjectDisposedException>(() => _ = box.Scene);
            Assert.Equal(bounds, box.Bounds);
            Assert.Equal(contentBounds, box.ContentBounds);
            Assert.Equal(semanticBounds, box.GetSemanticBounds(0));
            Assert.Same(semantics, box.SemanticItems);
            Assert.Same(semanticStyle, box.ResolveSemanticTextStyle(0));

            box.AdoptPreparedReplacement(replacement);

            Assert.False(box.IsGraphDetached);
            Assert.True(box.IsNativeResourcesPrepared);
            Assert.Same(replacementScene, box.Scene);
            Assert.False(box.GraphRebindFailed);
            Assert.Equal(bounds, box.Bounds);
            Assert.Equal(contentBounds, box.ContentBounds);
            Assert.Equal(semanticBounds, box.GetSemanticBounds(0));
            Assert.Same(semantics, box.SemanticItems);
            Assert.Same(semanticStyle, box.ResolveSemanticTextStyle(0));
            AssertFocusItem(expectedFocus, Assert.Single(snapshot.CollectFocusableItems()));
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    [Fact]
    public void CapturedPreparationCanStageAfterOwnerDisposalWithoutRetainingTheOldGraph()
    {
        var (box, snapshot, graph) = CreateMeasuredSemanticBox();
        VectorSceneDrawing? staged = null;
        try
        {
            VectorScenePreparation preparation = VectorScenePreparation.Capture(box);
            box.ReleaseSceneGraph();
            box.Dispose();

            AssertSceneGraphCollected(graph, box, snapshot, preparation);

            staged = preparation.Create(CreateScene(), CancellationToken.None);
            Assert.True(staged.HasScene);
            Assert.True(staged.IsPrepared);
            Assert.Throws<ObjectDisposedException>(() => box.AdoptPreparedReplacement(staged));
            Assert.True(staged.HasScene);
            Assert.True(staged.IsPrepared);
        }
        finally
        {
            staged?.Dispose();
            snapshot.Dispose();
        }
    }

    [Fact]
    public void AttachedOrDisposedOwnerRejectsReplacementAndLeavesStaleCandidateCallerOwned()
    {
        var (attached, attachedSnapshot, _) = CreateMeasuredSemanticBox();
        var (detached, detachedSnapshot, _) = CreateMeasuredSemanticBox();
        VectorSceneDrawing? staleCandidate = null;
        try
        {
            MarkdownVectorScene candidateScene = CreateScene();
            VectorScenePreparation attachedPreparation = VectorScenePreparation.Capture(attached);
            staleCandidate = attachedPreparation.Create(candidateScene, CancellationToken.None);
            Assert.Throws<InvalidOperationException>(() => attached.AdoptPreparedReplacement(staleCandidate));
            Assert.True(staleCandidate.HasScene);
            Assert.True(staleCandidate.IsPrepared);
            staleCandidate.Dispose();
            staleCandidate = null;

            VectorScenePreparation detachedPreparation = VectorScenePreparation.Capture(detached);
            detached.ReleaseSceneGraph();
            staleCandidate = detachedPreparation.Create(candidateScene, CancellationToken.None);
            Assert.Throws<InvalidOperationException>(() => attached.AdoptPreparedReplacement(staleCandidate));
            Assert.True(staleCandidate.HasScene);
            Assert.True(staleCandidate.IsPrepared);
            staleCandidate.Dispose();
            staleCandidate = null;

            staleCandidate = detachedPreparation.Create(CreateScene(), CancellationToken.None);
            detached.Dispose();
            Assert.Throws<ObjectDisposedException>(() => detached.AdoptPreparedReplacement(staleCandidate));
            Assert.True(staleCandidate.HasScene);
            Assert.True(staleCandidate.IsPrepared);
            staleCandidate.Dispose();
            staleCandidate = null;
        }
        finally
        {
            staleCandidate?.Dispose();
            attachedSnapshot.Dispose();
            detachedSnapshot.Dispose();
        }
    }

    [Fact]
    public void DetachedFallbackIsCreatedOnlyByPreparationAndReleasedWithResidency()
    {
        var (box, snapshot, _) = CreateMeasuredSemanticBox(
            source: new string('x', 500));
        try
        {
            box.ReleaseSceneGraph();
            Assert.True(box.IsGraphDetached);

            // Paint before a visible-band preparation remains a no-op and must
            // not manufacture a native text layout on the render callback.
            box.Paint(ds: null!, viewport: box.Bounds);

            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() =>
                    box.PrepareDetachedFallback(canceled.Token, MarkdownDetachedSceneFallback.Create));
            }
            Assert.True(box.PrepareDetachedFallback(CancellationToken.None, MarkdownDetachedSceneFallback.Create));
            Assert.False(box.PrepareDetachedFallback(CancellationToken.None, MarkdownDetachedSceneFallback.Create));

            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 160, 100, 96);
            using (CanvasDrawingSession drawingSession = target.CreateDrawingSession())
                box.Paint(drawingSession, box.Bounds);

            box.MarkGraphRebindFailed();
            Assert.True(box.GraphRebindFailed);
            Assert.True(box.PrepareDetachedFallback(
                CancellationToken.None,
                MarkdownDetachedSceneFallback.CreateFailed,
                replaceExisting: true));
            Assert.False(box.PrepareDetachedFallback(CancellationToken.None, MarkdownDetachedSceneFallback.Create));

            box.ReleaseNativeResources();
            Assert.True(box.IsGraphDetached);
            Assert.True(box.PrepareDetachedFallback(CancellationToken.None, MarkdownDetachedSceneFallback.Create));
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    [Fact]
    public void FailedFallbackReplacementPreservesPreviousPixelsAndDisposesCanceledCandidate()
    {
        var (box, snapshot, _) = CreateMeasuredSemanticBox(source: new string('x', 500));
        CanvasTextLayout? canceledCandidate = null;
        try
        {
            box.ReleaseSceneGraph();
            Assert.True(box.PrepareDetachedFallback(CancellationToken.None, MarkdownDetachedSceneFallback.Create));
            Color[] sourceFallback = RenderFallback(box);
            Assert.Contains(sourceFallback, pixel => pixel != Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

            Assert.False(box.PrepareDetachedFallback(
                CancellationToken.None,
                static (_, _) => null,
                replaceExisting: true));
            Assert.Equal(sourceFallback, RenderFallback(box));

            Assert.Throws<InvalidOperationException>(() => box.PrepareDetachedFallback(
                CancellationToken.None,
                static (_, _) => throw new InvalidOperationException("Injected fallback failure."),
                replaceExisting: true));
            Assert.Equal(sourceFallback, RenderFallback(box));

            using (var canceled = new CancellationTokenSource())
            {
                Assert.ThrowsAny<OperationCanceledException>(() => box.PrepareDetachedFallback(
                    canceled.Token,
                    (target, token) =>
                    {
                        canceledCandidate = MarkdownDetachedSceneFallback.Create(target, token);
                        Assert.NotNull(canceledCandidate);
                        canceled.Cancel();
                        return canceledCandidate;
                    },
                    replaceExisting: true));
            }

            Assert.NotNull(canceledCandidate);
            AssertTextLayoutDisposed(canceledCandidate!);
            canceledCandidate = null;
            Assert.Equal(sourceFallback, RenderFallback(box));

            // The forced-failure factory does not depend on the volatile flag,
            // so callers can stage the typed fallback before publishing it.
            Assert.True(box.PrepareDetachedFallback(
                CancellationToken.None,
                MarkdownDetachedSceneFallback.CreateFailed,
                replaceExisting: true));
            Assert.False(box.GraphRebindFailed);
            Color[] failedFallback = RenderFallback(box);
            Assert.NotEqual(sourceFallback, failedFallback);

            box.MarkGraphRebindFailed();
            Assert.True(box.GraphRebindFailed);
            Assert.Equal(failedFallback, RenderFallback(box));
        }
        finally
        {
            canceledCandidate?.Dispose();
            snapshot.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (VectorSceneBox Box, LayoutSnapshot Snapshot, WeakSceneGraph Graph)
        CreateMeasuredSemanticBox(string? source = null)
    {
        source ??= Source;
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        var context = CreateContext(device, source);
        MarkdownVectorScene scene = CreateScene();
        WeakSceneGraph graph = CreateWeakSceneGraph(scene);
        int sourceStart = Source.IndexOf("diagram", StringComparison.Ordinal);
        const int sourceLength = 14;
        var content = new MarkdownContentBuilder();
        content.AddVectorScene(
            scene,
            new SourceSpan(sourceStart, sourceLength),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "Search diagram",
            accessibilityDescription: "Search results by stage",
            semanticText: "diagram semantic copy");
        var box = new VectorSceneBox(
            context,
            Assert.Single(content.Build().Items),
            MarkdownElementKeys.Diagram)
        {
            BlockIndex = 0,
        };
        float height = box.Measure(160);
        box.Arrange(4, 8, 160);
        var snapshot = new LayoutSnapshot(
            [box],
            context.SourceMap,
            width: 160,
            height: height);
        return (box, snapshot, graph);
    }

    private static MarkdownLayoutContext CreateContext(CanvasDevice device, string source)
    {
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
        return new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap(source),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }

    private static MarkdownVectorScene CreateScene()
    {
        MarkdownVectorSemanticItem semantic = new(
            1,
            MarkdownVectorSemanticRole.Node,
            "Build stage",
            "The build stage produces search results.",
            parentIndex: -1,
            new SourceSpan(0, 1),
            new MarkdownVectorRectangle(18, 8, 42, 26),
            MarkdownVectorSemanticFlags.Focusable |
                MarkdownVectorSemanticFlags.Selectable |
                MarkdownVectorSemanticFlags.Linked);
        MarkdownVectorPathOperation[] path =
        [
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(0, 0)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(96, 0)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(96, 48)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(0, 48)),
            MarkdownVectorPathOperation.Close(),
        ];
        return new MarkdownVectorScene(
            width: 96,
            height: 48,
            baseline: 40,
            commands: [MarkdownVectorCommand.FillPath(path)],
            referenceFontSize: 16,
            viewport: new MarkdownVectorRectangle(0, 0, 96, 48),
            semantics: [semantic],
            links: [new MarkdownVectorLinkAction(0, "https://example.test/build", action: null, external: true)]);
    }

    private static void AssertFocusItem(FocusableItem expected, FocusableItem actual)
    {
        Assert.Equal(expected.BlockIndex, actual.BlockIndex);
        Assert.Equal(expected.InlineIndex, actual.InlineIndex);
        Assert.Equal(expected.Kind, actual.Kind);
    }

    private static void AssertSceneGraphCollected(WeakSceneGraph graph, params object[] owners)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!IsSceneGraphAlive(graph))
            {
                GC.KeepAlive(owners);
                return;
            }

            Thread.Sleep(10);
        }

        GC.KeepAlive(owners);
        Assert.False(IsSceneGraphAlive(graph), "A detached VectorSceneBox retained its command graph.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsSceneGraphAlive(WeakSceneGraph graph) =>
        graph.Scene.TryGetTarget(out _) ||
        graph.Commands.TryGetTarget(out _) ||
        graph.Command.TryGetTarget(out _) ||
        graph.Path.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakSceneGraph CreateWeakSceneGraph(MarkdownVectorScene scene)
    {
        MarkdownVectorCommand command = scene.Commands[0];
        return new WeakSceneGraph(
            new WeakReference<MarkdownVectorScene>(scene),
            new WeakReference<IReadOnlyList<MarkdownVectorCommand>>(scene.Commands),
            new WeakReference<MarkdownVectorCommand>(command),
            new WeakReference<IReadOnlyList<MarkdownVectorPathOperation>>(command.Path));
    }

    private static Color[] RenderFallback(VectorSceneBox box)
    {
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 160, 100, 96);
        using (CanvasDrawingSession drawingSession = target.CreateDrawingSession())
        {
            drawingSession.Clear(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            box.Paint(drawingSession, box.Bounds);
        }

        return target.GetPixelColors();
    }

    private static void AssertTextLayoutDisposed(CanvasTextLayout layout)
    {
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 8, 8, 96);
        using CanvasDrawingSession drawingSession = target.CreateDrawingSession();
        Assert.Throws<ObjectDisposedException>(() =>
            drawingSession.DrawTextLayout(layout, 0, 0, Color.FromArgb(0xFF, 0, 0, 0)));
    }

    private sealed class WeakSceneGraph(
        WeakReference<MarkdownVectorScene> scene,
        WeakReference<IReadOnlyList<MarkdownVectorCommand>> commands,
        WeakReference<MarkdownVectorCommand> command,
        WeakReference<IReadOnlyList<MarkdownVectorPathOperation>> path)
    {
        internal WeakReference<MarkdownVectorScene> Scene { get; } = scene;
        internal WeakReference<IReadOnlyList<MarkdownVectorCommand>> Commands { get; } = commands;
        internal WeakReference<MarkdownVectorCommand> Command { get; } = command;
        internal WeakReference<IReadOnlyList<MarkdownVectorPathOperation>> Path { get; } = path;
    }
}
