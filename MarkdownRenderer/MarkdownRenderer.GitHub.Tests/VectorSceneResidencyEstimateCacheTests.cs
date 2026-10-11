using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class VectorSceneResidencyEstimateCacheTests
{
    [Fact]
    public void SharedSceneWeightIsComputedOnceAcrossConcurrentPolicies()
    {
        MarkdownVectorScene scene = CreateScene();
        MarkdownVectorSceneResidencyPolicy[] policies = Enumerable.Range(0, 16)
            .Select(_ => new MarkdownVectorSceneResidencyPolicy([])).ToArray();
        try
        {
            Func<MarkdownVectorScene, long>[] estimates = policies.Select(CreateEstimateDelegate).ToArray();
            long[] weights = new long[estimates.Length];
            Parallel.For(0, estimates.Length, index => weights[index] = estimates[index](scene));
            long expected = 256 + 256 + 2048 + scene.Commands[0].Path!.Count * 96;
            Assert.All(weights, weight => Assert.Equal(expected, weight));
            Assert.Equal(1, policies.Sum(policy => policy.CommandGraphEstimateCount));
        }
        finally
        {
            foreach (var policy in policies) policy.Dispose();
        }
    }

    [Fact]
    public void WarmSceneWeightLookupDoesNotAllocateOrRewalkCommands()
    {
        MarkdownVectorScene scene = CreateScene();
        var policy = new MarkdownVectorSceneResidencyPolicy([]);
        try
        {
            Func<MarkdownVectorScene, long> estimate = CreateEstimateDelegate(policy);
            long expected = estimate(scene);
            for (int index = 0; index < 1000; index++) _ = estimate(scene);
            long observed = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10000; index++) observed = estimate(scene);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(expected, observed);
            Assert.Equal(1, policy.CommandGraphEstimateCount);
            Assert.Equal(0, allocated);
        }
        finally
        {
            policy.Dispose();
        }
    }

    private static Func<MarkdownVectorScene, long> CreateEstimateDelegate(MarkdownVectorSceneResidencyPolicy policy)
    {
        MethodInfo method = typeof(MarkdownVectorSceneResidencyPolicy).GetMethod(
            "GetPreparedResourceEstimate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(method);
        return method.CreateDelegate<Func<MarkdownVectorScene, long>>(policy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneWeightIsMemoizedAcrossOwnersAndBandExitReentry(bool inline)
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownLayoutContext context = CreateContext(session);
        MarkdownVectorScene scene = CreateScene();
        BlockBox first = inline
            ? CreateInlineBox(context, scene, blockIndex: 0)
            : CreateVectorBox(context, scene, blockIndex: 0);
        BlockBox second = inline
            ? CreateInlineBox(context, scene, blockIndex: 1)
            : CreateVectorBox(context, scene, blockIndex: 1);

        try
        {
            first.Measure(320);
            first.Arrange(0, 0, 320);
            second.Measure(320);
            second.Arrange(0, 100, 320);

            BlockBox[] blocks = [first, second];
            VectorScenePolicyTestPlacements placements = VectorScenePolicyTestPlacements.Create(blocks);
            var policy = new MarkdownVectorSceneResidencyPolicy(blocks);
            try
            {
                LazyLayoutBand firstBand = GetBand(first);
                LazyLayoutBand secondBand = GetBand(second);
                policy.ReplaceActiveBand([0], firstBand, placements.Generation,
                    placements.OrdinalByBlockIndex, CancellationToken.None);

                int expectedWalkCount = 1;
                long expectedWeight = policy.ActiveEstimatedBytes;
                Assert.True(expectedWeight > 0);
                Assert.Equal(expectedWalkCount, policy.CommandGraphEstimateCount);

                // The same immutable scene can be exposed by distinct layout
                // owners. Its command-derived scalar weight does not change
                // when the active band releases and later reacquires an owner.
                policy.ReplaceActiveBand([1], secondBand, placements.Generation,
                    placements.OrdinalByBlockIndex, CancellationToken.None);
                Assert.Equal(expectedWalkCount, policy.CommandGraphEstimateCount);
                Assert.Equal(expectedWeight, policy.ActiveEstimatedBytes);

                policy.ReplaceActiveBand([], new LazyLayoutBand(400, 500), placements.Generation,
                    placements.OrdinalByBlockIndex, CancellationToken.None);
                Assert.Equal(0, policy.ActiveEstimatedBytes);

                policy.ReplaceActiveBand([0], firstBand, placements.Generation,
                    placements.OrdinalByBlockIndex, CancellationToken.None);
                Assert.Equal(expectedWalkCount, policy.CommandGraphEstimateCount);
                Assert.Equal(expectedWeight, policy.ActiveEstimatedBytes);
            }
            finally
            {
                policy.Dispose();
            }
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public void WeakSceneWeightMemoizationAndRetiredPolicyDoNotRootSceneOrCommands()
    {
        MarkdownVectorSceneResidencyPolicy policy = CreateRetiredPolicy(
            out WeakReference<MarkdownVectorScene> sceneReference,
            out WeakReference<IReadOnlyList<MarkdownVectorCommand>> commandsReference);

        ForceFullCollection();

        Assert.False(sceneReference.TryGetTarget(out _));
        Assert.False(commandsReference.TryGetTarget(out _));
        GC.KeepAlive(policy);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MarkdownVectorSceneResidencyPolicy CreateRetiredPolicy(
        out WeakReference<MarkdownVectorScene> sceneReference,
        out WeakReference<IReadOnlyList<MarkdownVectorCommand>> commandsReference)
    {
        MarkdownVectorScene scene = CreateScene();
        var box = CreateVectorBox(CreateContext(performanceSession: null), scene, blockIndex: 0);
        box.Measure(320);
        box.Arrange(0, 0, 320);
        sceneReference = new WeakReference<MarkdownVectorScene>(scene);
        commandsReference = new WeakReference<IReadOnlyList<MarkdownVectorCommand>>(scene.Commands);

        BlockBox[] blocks = [box];
        var placements = VectorScenePolicyTestPlacements.Create(blocks);
        var policy = new MarkdownVectorSceneResidencyPolicy(blocks);
        policy.ReplaceActiveBand([0], GetBand(box), placements.Generation,
            placements.OrdinalByBlockIndex, CancellationToken.None);
        policy.Dispose();
        return policy;
    }

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static LazyLayoutBand GetBand(BlockBox box)
        => new(box.Bounds.Top, box.Bounds.Bottom);

    private static VectorSceneBox CreateVectorBox(
        MarkdownLayoutContext context,
        MarkdownVectorScene scene,
        int blockIndex)
    {
        var contentBuilder = new MarkdownContentBuilder();
        contentBuilder.AddVectorScene(
            scene,
            new SourceSpan(blockIndex, 1),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "Scene estimate test");
        return new VectorSceneBox(
            context,
            Assert.Single(contentBuilder.Build().Items),
            MarkdownElementKeys.Diagram)
        {
            BlockIndex = blockIndex,
        };
    }

    private static InlineContainerBox CreateInlineBox(
        MarkdownLayoutContext context,
        MarkdownVectorScene scene,
        int blockIndex)
    {
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = blockIndex,
        };
        box.Add(new InlineVectorSceneRun(
            context,
            scene,
            semanticText: "x",
            accessibilityName: "Inline scene estimate test",
            accessibilityDescription: null,
            elementKey: MarkdownElementKeys.Math)
        {
            DeferredSceneMarker = $"estimate-scene:{blockIndex}",
        });
        return box;
    }

    private static MarkdownVectorScene CreateScene()
    {
        var operations = new List<MarkdownVectorPathOperation>
        {
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(0, 0)),
        };
        for (int index = 0; index < 64; index++)
            operations.Add(MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(index, index % 17)));
        operations.Add(MarkdownVectorPathOperation.Close());
        return new MarkdownVectorScene(
            width: 64,
            height: 32,
            baseline: 24,
            commands: [MarkdownVectorCommand.FillPath(operations)]);
    }

    private static MarkdownLayoutContext CreateContext(MarkdownPerformanceSession? performanceSession)
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
            [MarkdownElementKeys.Math] = style,
            [MarkdownElementKeys.Diagram] = style,
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
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(new string(' ', 32)),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight)
        {
            PerformanceSession = performanceSession is null
                ? null
                : (IMarkdownPerformanceSessionInternal)performanceSession,
            RasterizationScale = 1,
        };
        if (performanceSession is not null)
            context.UseProgressiveNativeSceneResidency = performanceSession.Options.DeferOffscreenScenes;
        return context;
    }
}
