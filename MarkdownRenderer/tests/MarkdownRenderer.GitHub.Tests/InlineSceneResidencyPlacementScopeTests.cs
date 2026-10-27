using System.Collections.Generic;
using System.Threading;
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

public sealed class InlineSceneResidencyPlacementScopeTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(1.75f)]
    [InlineData(2f)]
    [InlineData(2.25f)]
    [InlineData(3f)]
    [InlineData(4f)]
    public void ReadinessUsesTheSameFractionalRootPlacementAsBandPreparation(float rasterizationScale)
    {
        const float localTop = 0f;
        const float placementTranslationY = 0.2f;

        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var style = new ElementStyle
        {
            FontSize = 16,
            Foreground = Color.FromArgb(255, 20, 20, 20),
        };
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = style,
                [MarkdownElementKeys.Math] = style,
            },
            new Dictionary<string, ElementStyleOverride>(),
            Color.FromArgb(255, 255, 255, 255),
            Color.FromArgb(255, 0, 80, 180),
            Color.FromArgb(255, 255, 255, 255),
            style.Foreground,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        var admission = new VectorNativeResourceAdmission(1_000_000, 1_000_000);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap("inline scene"),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight)
        {
            RasterizationScale = rasterizationScale,
            PerformanceSession = (IMarkdownPerformanceSessionInternal)session,
            UseProgressiveNativeSceneResidency = true,
        };
        var inline = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = 1,
        };
        var run = new InlineVectorSceneRun(
            context,
            CreateScene(),
            "inline badge",
            "inline badge",
            null,
            MarkdownElementKeys.Math,
            nativeAdmission: admission);
        inline.Add(run);
        _ = inline.Measure(160);
        inline.Arrange(0, localTop, 160);

        LayoutPlacementGeneration placements = LayoutPlacementGeneration.Create(
            [new LayoutPlacementInput(
                baseX: 0,
                baseTop: localTop,
                logicalX: 0,
                width: 160,
                height: (float)inline.Bounds.Height,
                measured: true,
                sharesFlowWithNext: false)],
            initialTop: localTop + placementTranslationY,
            blockSpacing: 0,
            bottomPadding: 0,
            documentWidth: 160,
            revision: 1);
        LayoutRootPlacement placement = placements.GetPlacement(0);
        Assert.Equal(placementTranslationY, placement.TranslationY);
        var band = new LazyLayoutBand(placement.Top, placement.Bottom);
        var ordinalByBlockIndex = new Dictionary<int, int> { [inline.BlockIndex] = 0 };
        var policy = new MarkdownVectorSceneResidencyPolicy([inline], nativeAdmission: admission);
        try
        {
            // The index is collected while ReplaceActiveBand enters this root's
            // placement scope. Pixel snapping must therefore use the same scope
            // during the subsequent non-mutating readiness probe.
            policy.ReplaceActiveBand([0], band, placements, ordinalByBlockIndex, CancellationToken.None);
            Assert.True(run.IsNativeResourcesPrepared);
            Assert.False(policy.HasCapacityDeferredScenes);

            inline.GetSceneGeometryOrigin(out _, out double unplacedOriginY);
            double placedOriginY;
            using (LayoutPlacementTransformContext.Enter(placement.TranslationX, placement.TranslationY))
            {
                inline.GetSceneGeometryOrigin(out _, out placedOriginY);
            }
            Assert.NotEqual(unplacedOriginY, placedOriginY);

            int indexBuildsBeforeReadiness = policy.InlineGeometryIndexBuildCount;
            bool settled = policy.IsBandWorkSettled([0], band, placements, ordinalByBlockIndex);
            bool prepared = policy.AreNativeResourcesPrepared([0], band, placements, ordinalByBlockIndex);
            Assert.True(settled && prepared,
                $"Readiness must reuse the prepared placement context at {rasterizationScale}x; " +
                $"unplaced origin={unplacedOriginY:R}, placed origin={placedOriginY:R}, " +
                $"settled={settled}, prepared={prepared}.");
            Assert.Equal(indexBuildsBeforeReadiness, policy.InlineGeometryIndexBuildCount);

            // A caller-provided ordinal is validated before placement lookup;
            // malformed ordinals must remain a conservative not-ready result.
            Assert.False(policy.IsBandWorkSettled([-1], band, placements, ordinalByBlockIndex));
            Assert.False(policy.AreNativeResourcesPrepared(
                [placements.Count], band, placements, ordinalByBlockIndex));
        }
        finally
        {
            try
            {
                policy.Dispose();
            }
            finally
            {
                inline.Dispose();
            }
        }
        Assert.Equal(0, admission.ReservedWeightUnits);
    }

    private static MarkdownVectorScene CreateScene() => new(
        width: 40,
        height: 40,
        baseline: 40,
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
}
