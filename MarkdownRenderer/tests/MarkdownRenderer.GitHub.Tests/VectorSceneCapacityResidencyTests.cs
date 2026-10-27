using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class VectorSceneCapacityResidencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BandExchangePublishesReadableFallbackThenRetriesAfterOutgoingCreditRefund(bool inline)
    {
        using var fixture = new Fixture(inline);
        fixture.ReplaceActiveBand([0], fixture.Band(0), CancellationToken.None);
        Assert.True(fixture.IsPrepared(0));
        Assert.False(fixture.IsPrepared(1));

        long previousGeneration = fixture.Admission.CapacityGeneration;
        fixture.ReplaceActiveBand([1], fixture.Band(1), CancellationToken.None);

        Assert.False(fixture.IsPrepared(0));
        Assert.False(fixture.IsPrepared(1));
        Assert.True(fixture.IsFallback(1));
        Assert.True(fixture.IsBandWorkSettled([1], fixture.Band(1)));
        Assert.False(fixture.AreNativeResourcesPrepared([1], fixture.Band(1)));
        Assert.True(fixture.Policy.HasCapacityDeferredScenes);
        Assert.Equal(previousGeneration, fixture.Policy.ActiveCapacityGeneration);
        Assert.True(fixture.Admission.CapacityGeneration > previousGeneration);
        Assert.Equal(1, fixture.Policy.ActiveSceneCount);

        // The second pass is a stationary, generation-triggered retry, not a
        // new viewport or a required input event. It must now acquire credit.
        fixture.ReplaceActiveBand([1], fixture.Band(1), CancellationToken.None);
        Assert.True(fixture.IsPrepared(1));
        Assert.False(fixture.IsFallback(1));
        Assert.False(fixture.Policy.HasCapacityDeferredScenes);
        Assert.True(fixture.AreNativeResourcesPrepared([1], fixture.Band(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapacityFallbackIsSettledButNeverMasqueradesAsNativeReady(bool inline)
    {
        using var fixture = new Fixture(inline);
        LazyLayoutBand both = new(fixture.Blocks[0].Bounds.Top, fixture.Blocks[1].Bounds.Bottom);
        fixture.ReplaceActiveBand([0, 1], both, CancellationToken.None);

        Assert.True(fixture.IsPrepared(0));
        Assert.True(fixture.IsFallback(1));
        Assert.True(fixture.IsBandWorkSettled([0, 1], both));
        Assert.False(fixture.AreNativeResourcesPrepared([0, 1], both));
        Assert.False(fixture.HasFailedScenes(both));
        Assert.True(fixture.Policy.HasCapacityDeferredScenes);
        Assert.Equal(2, fixture.Policy.ActiveSceneCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PermanentlyOversizedSceneKeepsFallbackWithoutCapacityWaiterState(bool inline)
    {
        using var fixture = new Fixture(inline, capacityAdjustment: -1);
        fixture.ReplaceActiveBand([0], fixture.Band(0), CancellationToken.None);
        Assert.True(fixture.IsFallback(0));
        Assert.Equal(VectorNativePreparationStatus.TooLarge, fixture.Status(0));
        Assert.True(fixture.IsBandWorkSettled([0], fixture.Band(0)));
        Assert.False(fixture.AreNativeResourcesPrepared([0], fixture.Band(0)));
        Assert.False(fixture.Policy.HasCapacityDeferredScenes);
        Assert.False(fixture.HasFailedScenes(fixture.Band(0)));
        fixture.ReplaceActiveBand([0], fixture.Band(0), CancellationToken.None);
        Assert.True(fixture.IsFallback(0));
        Assert.Equal(VectorNativePreparationStatus.TooLarge, fixture.Status(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanceledIncomingBandDoesNotStealTheCommittedOwnersCredit(bool inline)
    {
        using var fixture = new Fixture(inline);
        fixture.ReplaceActiveBand([0], fixture.Band(0), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            fixture.ReplaceActiveBand([1], fixture.Band(1), cancellation.Token));
        Assert.True(fixture.IsPrepared(0));
        Assert.False(fixture.IsPrepared(1));
        Assert.True(fixture.AreNativeResourcesPrepared([0], fixture.Band(0)));
        Assert.Equal(1, fixture.Policy.ActiveSceneCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentPoliciesShareAdmissionAndCanRecoverWithoutViewportMovement(bool inline)
    {
        var admission = new VectorNativeResourceAdmission(SceneWeight, SceneWeight);
        using var first = new Fixture(inline, admission: admission);
        using var second = new Fixture(inline, admission: admission);
        first.ReplaceActiveBand([0], first.Band(0), CancellationToken.None);
        second.ReplaceActiveBand([0], second.Band(0), CancellationToken.None);
        Assert.True(first.IsPrepared(0));
        Assert.True(second.IsFallback(0));
        long failedGeneration = second.Policy.ActiveCapacityGeneration;

        first.Policy.Dispose();
        Assert.True(admission.CapacityGeneration > failedGeneration);
        second.ReplaceActiveBand([0], second.Band(0), CancellationToken.None);
        Assert.True(second.IsPrepared(0));
        Assert.False(second.Policy.HasCapacityDeferredScenes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EagerSnapshotRefreshesVisibleResidencyWithoutReflowingOverscan(bool inline)
    {
        using var fixture = new Fixture(inline);
        using var snapshot = new LayoutSnapshot(fixture.Blocks,
            new MarkdownSourceMap("first scene\nsecond scene"), 160,
            (float)fixture.Blocks[1].Bounds.Bottom,
            vectorSceneResidencyPolicy: fixture.Policy);
        LazyLayoutBand realization = new(0, fixture.Blocks[1].Bounds.Bottom);
        Rect[] originalBounds = fixture.Blocks.Select(box => box.Bounds).ToArray();
        long revision = snapshot.LayoutRevision;

        LazyLayoutCommit first = snapshot.EnsureMeasuredBand(
            realization, fixture.Band(0), CancellationToken.None);
        Assert.True(first.NativeSceneResourcesChanged);
        Assert.True(fixture.IsPrepared(0));
        Assert.False(fixture.IsPrepared(1));
        Assert.True(snapshot.IsBandWorkSettled(realization, fixture.Band(0)));

        // Identical overscan must not absorb a different actual viewport. An
        // eager snapshot still exchanges resources, without Measure/Arrange.
        snapshot.EnsureMeasuredBand(realization, fixture.Band(1), CancellationToken.None);
        Assert.True(fixture.IsFallback(1));
        Assert.True(snapshot.HasCapacityDeferredScenes);
        Assert.True(snapshot.IsBandWorkSettled(realization, fixture.Band(1)));
        snapshot.EnsureMeasuredBand(realization, fixture.Band(1), CancellationToken.None);
        Assert.True(fixture.IsPrepared(1));
        Assert.False(snapshot.HasCapacityDeferredScenes);
        Assert.Equal(revision, snapshot.LayoutRevision);
        Assert.Equal(originalBounds, fixture.Blocks.Select(box => box.Bounds).ToArray());
        Assert.False(snapshot.GeometryRecoveryRequired);
    }

    [Fact]
    public void DetachedGraphReplacementKeepsSharedAdmissionAndCanAdoptTypedCapacityFallback()
    {
        var admission = new VectorNativeResourceAdmission(SceneWeight, SceneWeight);
        using var first = new Fixture(inline: false, admission: admission);
        using var incoming = new Fixture(inline: false, admission: admission);
        first.ReplaceActiveBand([0], first.Band(0), CancellationToken.None);
        var box = Assert.IsType<VectorSceneBox>(incoming.Blocks[0]);
        VectorScenePreparation recipe = VectorScenePreparation.Capture(box);
        box.ReleaseSceneGraph();
        incoming.ReplaceActiveBand([0], incoming.Band(0), CancellationToken.None);
        Assert.True(incoming.Policy.HasPendingGraphRebinds);
        Assert.True(box.HasDetachedFallback);

        VectorSceneDrawing? replacement = recipe.Create(CreateScene(), CancellationToken.None,
            deferNativeResources: true);
        try
        {
            Assert.Same(admission, replacement.NativeAdmission);
            Assert.False(replacement.TryPrepareNativeResources(CancellationToken.None, out var outcome));
            Assert.Equal(VectorNativePreparationStatus.DeferredCapacity, outcome.Status);
            Assert.True(recipe.PrepareCapacityFallback(CancellationToken.None));
            Assert.True(incoming.Policy.AdoptGraphRebind(box, incoming.Band(0), replacement,
                incoming.Policy.GetPreparedResourceEstimate(CreateScene())));
            replacement = null;
            Assert.False(box.IsGraphDetached);
            Assert.True(box.IsCapacityFallbackPaintable);
            Assert.False(box.GraphRebindFailed);
            Assert.True(incoming.Policy.HasCapacityDeferredScenes);
            Assert.True(incoming.IsBandWorkSettled([0], incoming.Band(0)));
            Assert.False(incoming.AreNativeResourcesPrepared([0], incoming.Band(0)));

            first.Policy.Dispose();
            incoming.ReplaceActiveBand([0], incoming.Band(0), CancellationToken.None);
            Assert.True(box.IsNativeResourcesPrepared);
            Assert.False(box.IsCapacityFallbackPaintable);
        }
        finally
        {
            replacement?.Dispose();
        }
    }

    private static long SceneWeight =>
        VectorSceneDrawing.EstimateNativeResourceWeightUnits(CreateScene(), CancellationToken.None);

    private static MarkdownVectorScene CreateScene() => new(
        width: 40, height: 40, baseline: 40,
        commands: new[]
        {
            MarkdownVectorCommand.FillPath(new[]
            {
                MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(0, 0)),
                MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(40, 0)),
                MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(20, 40)),
                MarkdownVectorPathOperation.Close(),
            }),
        });

    private sealed class Fixture : IDisposable
    {
        private readonly MarkdownPerformanceSession _session;
        private readonly object[] _owners;
        private readonly bool _ownsAdmission;
        private readonly VectorScenePolicyTestPlacements _placements;

        internal Fixture(bool inline, long capacityAdjustment = 0,
            VectorNativeResourceAdmission? admission = null)
        {
            _ownsAdmission = admission is null;
            Admission = admission ?? new VectorNativeResourceAdmission(
                SceneWeight + capacityAdjustment, SceneWeight + capacityAdjustment);
            _session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
            var style = new ElementStyle { FontSize = 16, Foreground = Color.FromArgb(255, 20, 20, 20) };
            var theme = new ThemeSnapshot(
                new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
                {
                    [MarkdownElementKeys.Body] = style, [MarkdownElementKeys.Math] = style,
                    [MarkdownElementKeys.Diagram] = style, [MarkdownElementKeys.Link] = style,
                },
                new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
                Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 0, 80, 180),
                Color.FromArgb(255, 255, 255, 255), style.Foreground,
                isDark: false, isHighContrast: false, textScaleFactor: 1);
            var context = new MarkdownLayoutContext(CanvasDevice.GetSharedDevice(), theme,
                new MarkdownSourceMap("first scene\nsecond scene"), new MarkdownExtensionRegistry(),
                FlowDirection.LeftToRight)
            {
                PerformanceSession = (IMarkdownPerformanceSessionInternal)_session,
                UseProgressiveNativeSceneResidency = true,
            };
            Blocks = new BlockBox[2];
            _owners = new object[2];
            for (int i = 0; i < Blocks.Length; i++)
            {
                if (inline)
                {
                    var run = new InlineVectorSceneRun(context, CreateScene(), $"scene {i}",
                        $"inline scene {i}", null, MarkdownElementKeys.Math, nativeAdmission: Admission);
                    var box = new InlineContainerBox(context, MarkdownElementKeys.Body) { BlockIndex = i + 1 };
                    box.Add(run);
                    Blocks[i] = box;
                    _owners[i] = run;
                }
                else
                {
                    var builder = new MarkdownContentBuilder();
                    builder.AddVectorScene(CreateScene(), new SourceSpan(i * 12, 11),
                        MarkdownStyleRole.Diagram, MarkdownAccessibilityRole.Diagram,
                        accessibilityName: $"scene {i}", semanticText: $"scene {i}");
                    var box = new VectorSceneBox(context, Assert.Single(builder.Build().Items),
                        MarkdownElementKeys.Diagram, nativeAdmission: Admission) { BlockIndex = i + 1 };
                    Blocks[i] = box;
                    _owners[i] = box;
                }
                Blocks[i].Measure(160);
                Blocks[i].Arrange(0, i * 120, 160);
            }
            _placements = VectorScenePolicyTestPlacements.Create(Blocks);
            Policy = new MarkdownVectorSceneResidencyPolicy(Blocks, nativeAdmission: Admission);
        }

        internal VectorNativeResourceAdmission Admission { get; }
        internal BlockBox[] Blocks { get; }
        internal MarkdownVectorSceneResidencyPolicy Policy { get; }
        internal bool ReplaceActiveBand(
            IReadOnlyList<int> ordinals,
            LazyLayoutBand band,
            CancellationToken cancellationToken)
            => Policy.ReplaceActiveBand(
                ordinals, band, _placements.Generation, _placements.OrdinalByBlockIndex, cancellationToken);
        internal bool IsBandWorkSettled(IReadOnlyList<int> ordinals, LazyLayoutBand band)
            => Policy.IsBandWorkSettled(
                ordinals, band, _placements.Generation, _placements.OrdinalByBlockIndex);
        internal bool AreNativeResourcesPrepared(IReadOnlyList<int> ordinals, LazyLayoutBand band)
            => Policy.AreNativeResourcesPrepared(
                ordinals, band, _placements.Generation, _placements.OrdinalByBlockIndex);
        internal bool HasFailedScenes(LazyLayoutBand band)
            => Policy.HasFailedScenes(band, _placements.Generation, _placements.OrdinalByBlockIndex);
        internal LazyLayoutBand Band(int index) => new(Blocks[index].Bounds.Top, Blocks[index].Bounds.Bottom);
        internal bool IsPrepared(int index) => _owners[index] switch
        {
            VectorSceneBox box => box.IsNativeResourcesPrepared,
            InlineVectorSceneRun run => run.IsNativeResourcesPrepared,
            _ => false,
        };
        internal bool IsFallback(int index) => _owners[index] switch
        {
            VectorSceneBox box => box.IsCapacityFallbackPaintable,
            InlineVectorSceneRun run => run.IsCapacityFallbackPaintable,
            _ => false,
        };
        internal VectorNativePreparationStatus Status(int index) => _owners[index] switch
        {
            VectorSceneBox box => box.NativePreparationStatus,
            InlineVectorSceneRun run => run.NativePreparationStatus,
            _ => VectorNativePreparationStatus.NotPrepared,
        };
        public void Dispose()
        {
            try { Policy.Dispose(); }
            finally
            {
                try { foreach (BlockBox box in Blocks) box.Dispose(); }
                finally { _session.Dispose(); }
            }
            if (_ownsAdmission)
                Assert.Equal(0, Admission.ReservedWeightUnits);
        }
    }
}
