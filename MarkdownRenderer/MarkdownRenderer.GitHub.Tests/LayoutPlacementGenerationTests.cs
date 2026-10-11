using System;
using System.Numerics;
using System.Threading;
using MarkdownRenderer.Layout;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class LayoutPlacementGenerationTests
{
    [Fact]
    public void PlacementTransformScopeSnapsAtFinalLogicalOriginAndRestoresNestedState()
    {
        const float scale = 1.5f;
        const float rootLocalX = 392f;
        const float rootLocalY = 127.25f;

        LayoutPlacementTransformContext.SnapOriginToDevicePixels(
            rootLocalX, rootLocalY, scale, out float unplacedX, out float unplacedY);
        Assert.Equal(MathF.Round(rootLocalX * scale) / scale, unplacedX);
        Assert.Equal(MathF.Round(rootLocalY * scale) / scale, unplacedY);

        using (LayoutPlacementTransformContext.Enter(0.302f, -0.417f))
        {
            LayoutPlacementTransformContext.SnapOriginToDevicePixels(
                rootLocalX, rootLocalY, scale, out float outerX, out float outerY);
            AssertClose(
                MathF.Round((rootLocalX + 0.302f) * scale) / scale,
                outerX + 0.302f);
            AssertClose(
                MathF.Round((rootLocalY - 0.417f) * scale) / scale,
                outerY - 0.417f);

            using (LayoutPlacementTransformContext.Enter(-0.187f, 0.913f))
            {
                LayoutPlacementTransformContext.GetCurrentTranslation(out float nestedX, out float nestedY);
                Assert.InRange(nestedX, 0.1149f, 0.1151f);
                Assert.InRange(nestedY, 0.4959f, 0.4961f);
                LayoutPlacementTransformContext.SnapOriginToDevicePixels(
                    rootLocalX, rootLocalY, scale, out float nestedLocalX, out float nestedLocalY);
                AssertClose(
                    MathF.Round((rootLocalX + nestedX) * scale) / scale,
                    nestedLocalX + nestedX);
                AssertClose(
                    MathF.Round((rootLocalY + nestedY) * scale) / scale,
                    nestedLocalY + nestedY);
            }

            LayoutPlacementTransformContext.GetCurrentTranslation(out float restoredX, out float restoredY);
            Assert.Equal(0.302f, restoredX);
            Assert.Equal(-0.417f, restoredY);
        }

        LayoutPlacementTransformContext.GetCurrentTranslation(out float clearedX, out float clearedY);
        Assert.Equal(0f, clearedX);
        Assert.Equal(0f, clearedY);
    }

    [Fact]
    public void RemovingPlacementPrefixPreservesNonIdentityCallerTransform()
    {
        var callerTransform = Matrix3x2.CreateScale(0.93f, 0.88f) *
            Matrix3x2.CreateRotation(0.023f) *
            Matrix3x2.CreateTranslation(12.25f, 7.75f);

        using (LayoutPlacementTransformContext.Enter(392.45346f, -23.1875f, callerTransform))
        {
            Assert.True(LayoutPlacementTransformContext.TryGetBaseCanvasTransform(out Matrix3x2 baseTransform));
            Assert.Equal(callerTransform, baseTransform);
            Matrix3x2 placedTransform =
                Matrix3x2.CreateTranslation(392.45346f, -23.1875f) * callerTransform;
            Assert.Equal(
                callerTransform,
                LayoutPlacementTransformContext.RemoveCurrentPlacementTranslation(placedTransform));

            using (LayoutPlacementTransformContext.Enter(-0.75f, 9.5f))
            {
                Assert.True(LayoutPlacementTransformContext.TryGetBaseCanvasTransform(out Matrix3x2 nestedBaseTransform));
                Assert.Equal(callerTransform, nestedBaseTransform);
                LayoutPlacementTransformContext.GetCurrentTranslation(out float translationX, out float translationY);
                Matrix3x2 nestedPlacedTransform =
                    Matrix3x2.CreateTranslation(translationX, translationY) * callerTransform;
                Assert.Equal(
                    callerTransform,
                    LayoutPlacementTransformContext.RemoveCurrentPlacementTranslation(nestedPlacedTransform));
            }
            Assert.True(LayoutPlacementTransformContext.TryGetBaseCanvasTransform(out Matrix3x2 restoredNestedBaseTransform));
            Assert.Equal(callerTransform, restoredNestedBaseTransform);
        }
        Assert.False(LayoutPlacementTransformContext.TryGetBaseCanvasTransform(out _));
    }

    private static void AssertClose(float expected, float actual)
        => Assert.InRange(actual, expected - 0.0001f, expected + 0.0001f);

    [Fact]
    public void PlacementTransformScopeRestoresAfterFailureAndIsThreadIsolated()
    {
        Action cancelInsidePlacementScope = () =>
        {
            using LayoutPlacementTransformContext.Scope scope =
                LayoutPlacementTransformContext.Enter(14.25f, -9.5f);
            LayoutPlacementTransformContext.GetCurrentTranslation(out float x, out float y);
            Assert.Equal(14.25f, x);
            Assert.Equal(-9.5f, y);
            throw new OperationCanceledException();
        };
        Assert.Throws<OperationCanceledException>(cancelInsidePlacementScope);

        LayoutPlacementTransformContext.GetCurrentTranslation(out float restoredX, out float restoredY);
        Assert.Equal(0f, restoredX);
        Assert.Equal(0f, restoredY);

        using LayoutPlacementTransformContext.Scope activeScope =
            LayoutPlacementTransformContext.Enter(4f, 8f);
        int ownerThreadId = Environment.CurrentManagedThreadId;
        int backgroundThreadId = 0;
        (float X, float Y) backgroundTranslation = default;
        var backgroundThread = new Thread(() =>
        {
            backgroundThreadId = Environment.CurrentManagedThreadId;
            LayoutPlacementTransformContext.GetCurrentTranslation(out float x, out float y);
            backgroundTranslation = (x, y);
        })
        {
            IsBackground = true,
        };
        backgroundThread.Start();
        Assert.True(backgroundThread.Join(TimeSpan.FromSeconds(5)), "The placement-isolation thread did not complete.");
        Assert.NotEqual(ownerThreadId, backgroundThreadId);
        Assert.Equal((0f, 0f), backgroundTranslation);
    }

    [Fact]
    public void Create_PreservesAdvanceBlockFloatOrderAndMapsRootLocalGeometry()
    {
        LayoutPlacementInput[] inputs =
        [
            new(5, -4, 12, 80, 10.25f, measured: true, sharesFlowWithNext: true),
            new(8, 3, 12, 80, 3.3f, measured: true, sharesFlowWithNext: false),
            new(0, 0, 12, 80, 4.4f, measured: false, sharesFlowWithNext: false),
        ];

        LayoutPlacementGeneration generation = LayoutPlacementGeneration.Create(
            inputs,
            initialTop: 7.25f,
            blockSpacing: 2.1f,
            bottomPadding: 6.5f,
            documentWidth: 100,
            revision: 17);

        float y = 7.25f;
        for (int ordinal = 0; ordinal < inputs.Length; ordinal++)
        {
            LayoutPlacementInput input = inputs[ordinal];
            LayoutRootPlacement placement = generation.GetPlacement(ordinal);
            Assert.Equal(input.LogicalX, placement.X);
            Assert.Equal(y, placement.Top);
            Assert.Equal(input.Height, placement.Height);
            Assert.Equal(input.Measured, placement.Measured);

            y += input.Height;
            if (ordinal < inputs.Length - 1 && !input.SharesFlowWithNext)
                y += 2.1f;
            Assert.Equal(y, generation.GetContinuation(ordinal));
        }
        y += 6.5f;
        Assert.Equal(y, generation.Size.Height);

        LayoutRootPlacement second = generation.GetPlacement(1);
        Rect physical = new(10, second.BaseTop + 6, 20, 9);
        Rect logical = generation.ToLogicalRect(1, physical);
        Assert.Equal((double)(second.X + ((float)physical.X - second.BaseX)), logical.X);
        Assert.Equal((double)(second.Top + ((float)physical.Y - second.BaseTop)), logical.Y);
        Point logicalPoint = new(logical.X + 2, logical.Y + 3);
        Assert.Equal(new Point((double)second.BaseX + (logicalPoint.X - second.X),
            (double)second.BaseTop + (logicalPoint.Y - second.Top)),
            generation.ToPhysicalPoint(1, logicalPoint));
        Assert.Equal(
            new Rect((double)second.BaseX + (logicalPoint.X - second.X),
                (double)second.BaseTop + (logicalPoint.Y - second.Top), 4, 5),
            generation.ToPhysicalViewport(1, new Rect(logicalPoint.X, logicalPoint.Y, 4, 5)));
    }

    [Fact]
    public void LogicalRectTranslationAndIndexedBottomMatchFreshFloatLayout()
    {
        const float documentTop = 213.28125f;
        const float localTop = 22.4f;
        LayoutPlacementInput[] inputs =
        [
            new(0, 0, 0, 100, 22.4f, measured: true, sharesFlowWithNext: false),
        ];

        LayoutPlacementGeneration generation = LayoutPlacementGeneration.Create(
            inputs,
            initialTop: documentTop,
            blockSpacing: 0,
            bottomPadding: 0,
            documentWidth: 100,
            revision: 1);
        LayoutRootPlacement placement = generation.GetPlacement(0);

        // The root's viewport edge follows the same single-precision flow
        // recurrence as a fresh Arrange.
        double expectedRootBottom = (double)(documentTop + placement.Height);
        Assert.Equal(expectedRootBottom, placement.Bottom);
        Assert.Equal(expectedRootBottom, generation.GetIndexedBounds(0).Bottom);

        Rect physical = new(0, localTop, 10, 8);
        Rect logical = generation.ToLogicalRect(0, physical);
        Assert.Equal((double)(placement.Top + (localTop - placement.BaseTop)), logical.Y);

        LayoutPlacementGeneration captured = LayoutPlacementGeneration.CaptureExisting(
            [new LayoutPlacementInput(0, documentTop, 0, 100, 22.4f, true, false)],
            new Size(100, 400),
            revision: 2,
            blockSpacing: 0);
        Assert.Equal(expectedRootBottom, captured.GetIndexedBounds(0).Bottom);
    }

    [Fact]
    public void CoordinateMappingPreservesRootOriginAcrossRoundedPlacementTranslation()
    {
        const float localOrigin = 173.7625f;
        const float logicalOrigin = 493.76248f;
        LayoutPlacementGeneration generation = LayoutPlacementGeneration.Create(
            [new LayoutPlacementInput(localOrigin, localOrigin, logicalOrigin, 100, 12, true, false)],
            initialTop: logicalOrigin,
            blockSpacing: 0,
            bottomPadding: 0,
            documentWidth: 600,
            revision: 1);
        LayoutRootPlacement placement = generation.GetPlacement(0);
        Assert.Equal(0x43F6E199, BitConverter.SingleToInt32Bits(placement.Top));
        Assert.Equal(320f, placement.TranslationY);

        Rect logicalRect = generation.ToLogicalRect(0,
            new Rect(localOrigin, localOrigin, 100, 12));
        Assert.Equal((double)logicalOrigin, logicalRect.X);
        Assert.Equal((double)logicalOrigin, logicalRect.Y);

        Point logicalPoint = generation.ToLogicalPoint(0,
            new Point(localOrigin, localOrigin));
        Assert.Equal(new Point(logicalOrigin, logicalOrigin), logicalPoint);

        Point physicalPoint = generation.ToPhysicalPoint(0,
            new Point(logicalOrigin, logicalOrigin));
        Assert.Equal(new Point(localOrigin, localOrigin), physicalPoint);
        Assert.Equal(new Rect(localOrigin, localOrigin, 20, 30),
            generation.ToPhysicalViewport(0,
                new Rect(logicalOrigin, logicalOrigin, 20, 30)));

        LazyLayoutBand physicalBand = generation.ToPhysicalBand(0,
            new LazyLayoutBand(logicalOrigin, logicalOrigin));
        Assert.Equal((double)localOrigin, physicalBand.Top);
        Assert.Equal((double)localOrigin, physicalBand.Bottom);

        // The old physical-origin-plus-delta expression lands one float ULP
        // beyond the fresh-layout position for this exact source origin.
        Assert.NotEqual(logicalOrigin,
            (float)localOrigin + placement.TranslationY);
    }

    [Fact]
    public void SuffixGenerationAndMeasuredMaskNeverMutateThePublishedGeneration()
    {
        LayoutPlacementInput[] initialInputs =
        [
            new(0, 0, 0, 100, 12.5f, measured: true, sharesFlowWithNext: false),
            new(0, 0, 0, 100, 20, measured: false, sharesFlowWithNext: false),
            new(0, 0, 0, 100, 30, measured: false, sharesFlowWithNext: false),
        ];
        LayoutPlacementGeneration published = LayoutPlacementGeneration.Create(
            initialInputs, 4, 3, 5, 100, revision: 2);
        LayoutRootPlacement publishedFirst = published.GetPlacement(0);
        float publishedPrefix = published.GetContinuation(0);

        LayoutPlacementInput[] changedInputs =
        [
            new(9, 9, 9, 77, 999, measured: false, sharesFlowWithNext: true),
            new(0, 0, 0, 100, 24, measured: true, sharesFlowWithNext: false),
            new(0, 0, 0, 100, 31, measured: false, sharesFlowWithNext: false),
        ];
        LayoutPlacementGeneration candidate = LayoutPlacementGeneration.Create(
            changedInputs,
            4,
            3,
            5,
            100,
            revision: 3,
            source: published,
            firstRecomputedOrdinal: 1);

        Assert.Equal(publishedFirst.X, candidate.GetPlacement(0).X);
        Assert.Equal(publishedFirst.Top, candidate.GetPlacement(0).Top);
        Assert.Equal(publishedFirst.Height, candidate.GetPlacement(0).Height);
        Assert.Equal(publishedPrefix, candidate.GetContinuation(0));
        Assert.Equal(publishedPrefix, published.GetContinuation(0));
        Assert.Equal(24, candidate.GetPlacement(1).Height);
        Assert.Equal(publishedPrefix + 24 + 3, candidate.GetPlacement(2).Top);

        bool[] measuredMask = [false, true, true];
        LayoutPlacementGeneration measured = candidate.WithMeasuredRoots(measuredMask, revision: 4);
        Assert.Equal(4L, measured.Revision);
        Assert.True(candidate.GetPlacement(1).Measured);
        Assert.False(candidate.GetPlacement(2).Measured);
        Assert.False(measured.GetPlacement(0).Measured);
        Assert.True(measured.GetPlacement(1).Measured);
        Assert.True(measured.GetPlacement(2).Measured);
        Assert.Equal(candidate.GetPlacement(2).Top, measured.GetPlacement(2).Top);
        ViewportRange candidateRange = candidate.Find(0, 100);
        ViewportRange measuredRange = measured.Find(0, 100);
        Assert.Equal(candidateRange.End - candidateRange.Start,
            measuredRange.End - measuredRange.Start);
    }
}
