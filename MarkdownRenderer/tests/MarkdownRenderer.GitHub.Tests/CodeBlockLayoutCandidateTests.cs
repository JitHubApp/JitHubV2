using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Markdig;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.CodeBlocks.Internal;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Focus;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.PrivatePlacement;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class CodeBlockLayoutCandidateTests
{
    internal const float LayoutWidth = 320f;
    internal const int AutomationGeneration = 7_421;

    [Theory]
    [InlineData(1f, false)]
    [InlineData(1f, true)]
    [InlineData(1.5f, false)]
    [InlineData(1.5f, true)]
    [InlineData(2f, false)]
    [InlineData(2f, true)]
    [InlineData(4f, false)]
    [InlineData(4f, true)]
    public async Task ParsedEagerCodeAndRasterCandidatePreservesHighlightGeometryAndCopyIdentity(
        float rasterizationScale,
        bool rightToLeft)
    {
        await using var document = await ParsedImageCodeDocument.CreateAsync(
            rasterizationScale, rightToLeft, lazy: false, spacerParagraphCount: 0);

        CodeBlockBox sourceCode = document.Code;
        CodeBlockHighlightCacheKey highlightKey = CreateHighlight(sourceCode, providerRevision: 1);
        Assert.True(sourceCode.HasAppliedSyntaxHighlighting(highlightKey));
        Assert.True(sourceCode.PrivatePlacementForegroundSpanCount > 0);
        Assert.True(sourceCode.CanScrollHorizontally);
        Assert.True(sourceCode.HasCopyButtonAction);
        Assert.True(sourceCode.CopyButtonBounds.Width > 0);

        MarkdownSemanticDocument sourceSemantic = document.Snapshot.SemanticDocument;
        Rect sourceBounds = sourceCode.Bounds;
        Rect sourceCopyBounds = RelativeTo(sourceCode.CopyButtonBounds, sourceCode.Bounds);
        Rect[] sourceChunkBounds = GetRelativeChunkBounds(sourceCode);
        FocusableItem sourceCopyAction = GetCodeCopyAction(document.Snapshot);
        long sourceCodeRevision = sourceCode.PrivatePlacementRevision;
        long sourceRevision = document.Snapshot.LayoutRevision;

        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? candidateContext = null;
        try
        {
            candidate = CreateCandidate(document, out candidateContext);
            SingleRasterLayoutCandidate owner = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
            await owner.DrainConstructionAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(owner.HasCurrentSourceRaster());
            Assert.Equal(1, owner.SourceImageCount);
            LayoutSnapshot replacement = owner.Snapshot;
            CodeBlockBox candidateCode = Assert.Single(replacement.Blocks.OfType<CodeBlockBox>());
            AssertCandidateCodeCopiesSourceIdentity(sourceCode, candidateCode, highlightKey);

            Assert.Equal(sourceBounds.Width, candidateCode.Bounds.Width, precision: 3);
            Assert.Equal(sourceBounds.Height, candidateCode.Bounds.Height, precision: 3);
            Assert.Equal(sourceCopyBounds, RelativeTo(candidateCode.CopyButtonBounds, candidateCode.Bounds));
            Assert.Equal(sourceChunkBounds, GetRelativeChunkBounds(candidateCode));
            Assert.True(candidateCode.CopyButtonBounds.Width > 0);
            Assert.Equal(
                PaintCodeBlock(document.Seed.Device, sourceCode),
                PaintCodeBlock(document.Seed.Device, candidateCode));

            FocusableItem candidateCopyAction = GetCodeCopyAction(replacement);
            Assert.Equal(sourceCopyAction.Kind, candidateCopyAction.Kind);
            Assert.Equal(sourceCopyAction.BlockIndex, candidateCopyAction.BlockIndex);
            Assert.Equal(sourceCopyAction.InlineIndex, candidateCopyAction.InlineIndex);
            Assert.Equal(sourceCode.BlockIndex, candidateCopyAction.BlockIndex);

            Assert.Same(document.SourceMap, replacement.SourceMap);
            Assert.Equal(sourceSemantic.Text, replacement.SemanticDocument.Text);
            Assert.Equal(sourceSemantic.TextProjectionFingerprint,
                replacement.SemanticDocument.TextProjectionFingerprint);
            Assert.Equal(sourceSemantic.SemanticRangeFingerprint,
                replacement.SemanticDocument.SemanticRangeFingerprint);
            Assert.Equal(AutomationGeneration, replacement.AutomationDocumentGeneration);
            Assert.Equal(sourceRevision, document.Snapshot.LayoutRevision);
            Assert.Equal(sourceBounds, sourceCode.Bounds);
            Assert.True(sourceCode.IsCurrentPrivatePlacement(sourceCodeRevision));
            Assert.True(owner.HasCurrentSourceRaster());
        }
        finally
        {
            await DisposeCandidateAndDrainAsync(candidate, candidateContext);
        }
    }

    private static void AssertCandidateCodeCopiesSourceIdentity(
        CodeBlockBox sourceCode,
        CodeBlockBox candidateCode,
        CodeBlockHighlightCacheKey highlightKey)
    {
        Assert.NotSame(sourceCode, candidateCode);
        Assert.Same(sourceCode.Metadata, candidateCode.Metadata);
        Assert.Same(sourceCode.CodeText, candidateCode.CodeText);
        Assert.Equal(sourceCode.StableKey, candidateCode.StableKey);
        Assert.Equal(sourceCode.CodeLanguage, candidateCode.CodeLanguage);
        Assert.Equal(sourceCode.HeaderText, candidateCode.HeaderText);
        Assert.Equal(sourceCode.ShowLineNumbers, candidateCode.ShowLineNumbers);
        Assert.Equal(sourceCode.LineCount, candidateCode.LineCount);
        Assert.Equal(sourceCode.HorizontalOffset, candidateCode.HorizontalOffset);
        Assert.Equal(sourceCode.HorizontalViewport, candidateCode.HorizontalViewport, precision: 3);
        Assert.Equal(sourceCode.HorizontalExtent, candidateCode.HorizontalExtent, precision: 3);
        Assert.True(candidateCode.HasAppliedSyntaxHighlighting(highlightKey));
        Assert.Equal(sourceCode.PrivatePlacementForegroundSpanCount,
            candidateCode.PrivatePlacementForegroundSpanCount);
        Assert.Equal(sourceCode.Chunks.Count, candidateCode.Chunks.Count);
        Assert.Equal(sourceCode.CodeText, ConcatenateChunkText(candidateCode));

        for (int index = 0; index < sourceCode.Chunks.Count; index++)
        {
            Assert.NotSame(sourceCode.Chunks[index], candidateCode.Chunks[index]);
            Assert.Equal(sourceCode.Chunks[index].CodeBlockTextOffset,
                candidateCode.Chunks[index].CodeBlockTextOffset);
            Assert.Equal(sourceCode.Chunks[index].CodeBlockTextLength,
                candidateCode.Chunks[index].CodeBlockTextLength);
        }
    }

    [Fact]
    public async Task ParsedOneLineFencedCodeEndingInLfUsesAutoMultilinePolicyAcrossCandidateRetirement()
    {
        await using var document = await ParsedImageCodeDocument.CreateAsync(
            rasterizationScale: 1f,
            rightToLeft: false,
            lazy: false,
            spacerParagraphCount: 0,
            singleLineCode: true,
            lineNumberMode: CodeBlockLineNumberMode.AutoMultiline);
        CodeBlockBox sourceCode = document.Code;
        Assert.Equal("let answer = 42;\n", sourceCode.CodeText);
        Assert.EndsWith("\n", sourceCode.CodeText);
        Assert.False(sourceCode.ShowLineNumbers);

        long sourceCodeRevision = sourceCode.PrivatePlacementRevision;
        var sourceProjection = document.Snapshot.SemanticDocument.TextProjectionFingerprint;
        var sourceRanges = document.Snapshot.SemanticDocument.SemanticRangeFingerprint;
        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? candidateContext = null;
        try
        {
            candidate = CreateCandidate(document, out candidateContext);
            SingleRasterLayoutCandidate owner = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
            await owner.DrainConstructionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(owner.HasCurrentSourceRaster());

            CodeBlockBox privateCode = Assert.Single(owner.Snapshot.Blocks.OfType<CodeBlockBox>());
            Assert.NotSame(sourceCode, privateCode);
            Assert.Same(sourceCode.Metadata, privateCode.Metadata);
            Assert.Same(sourceCode.CodeText, privateCode.CodeText);
            Assert.Equal("let answer = 42;\n", privateCode.CodeText);
            Assert.False(privateCode.ShowLineNumbers);
            Assert.Same(document.SourceMap, owner.Snapshot.SourceMap);
            Assert.Equal(sourceProjection, owner.Snapshot.SemanticDocument.TextProjectionFingerprint);
            Assert.Equal(sourceRanges, owner.Snapshot.SemanticDocument.SemanticRangeFingerprint);
            Assert.Equal(GetCodeCopyAction(document.Snapshot).BlockIndex,
                GetCodeCopyAction(owner.Snapshot).BlockIndex);
            Assert.True(sourceCode.IsCurrentPrivatePlacement(sourceCodeRevision));
        }
        finally
        {
            await DisposeCandidateAndDrainAsync(candidate, candidateContext);
        }

        // Retiring the private candidate must not invalidate the independent
        // source code box, its logical mapping, or its private revision fence.
        Assert.True(sourceCode.IsCurrentPrivatePlacement(sourceCodeRevision));
        Assert.Same(document.SourceMap, document.Snapshot.SourceMap);
        Assert.Equal(sourceProjection, document.Snapshot.SemanticDocument.TextProjectionFingerprint);
        Assert.Equal(sourceRanges, document.Snapshot.SemanticDocument.SemanticRangeFingerprint);
        Assert.False(sourceCode.ShowLineNumbers);
    }

    [Theory]
    [InlineData(1.5f, false)]
    [InlineData(4f, true)]
    public async Task ParsedLazyCandidateKeepsOffscreenCodeEstimatedThenRealizesPrivateHighlightedCode(
        float rasterizationScale,
        bool rightToLeft)
    {
        await using var document = await ParsedImageCodeDocument.CreateAsync(
            rasterizationScale, rightToLeft, lazy: true, spacerParagraphCount: 18);
        CodeBlockBox sourceCode = document.Code;
        CodeBlockHighlightCacheKey highlightKey = CreateHighlight(sourceCode, providerRevision: 2);
        Rect sourceEstimatedBounds = sourceCode.Bounds;
        int sourceMeasuredCount = document.Snapshot.MeasuredTopLevelBlockCount;

        Assert.True(document.Snapshot.IsLazyLayoutEnabled);
        Assert.False(document.Snapshot.IsTopLevelBlockMeasured(sourceCode));
        Assert.True(sourceCode.HasAppliedSyntaxHighlighting(highlightKey));
        Assert.True(sourceCode.PrivatePlacementForegroundSpanCount > 0);

        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? candidateContext = null;
        try
        {
            candidate = CreateCandidate(document, out candidateContext);
            SingleRasterLayoutCandidate owner = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
            await owner.DrainConstructionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            LayoutSnapshot replacement = owner.Snapshot;
            CodeBlockBox candidateCode = Assert.Single(replacement.Blocks.OfType<CodeBlockBox>());

            Assert.True(owner.HasCurrentSourceRaster());
            Assert.False(document.Snapshot.IsTopLevelBlockMeasured(sourceCode));
            Assert.False(replacement.IsTopLevelBlockMeasured(candidateCode));
            Assert.Equal(sourceMeasuredCount, replacement.MeasuredTopLevelBlockCount);
            Assert.Equal(sourceEstimatedBounds, candidateCode.Bounds);
            Assert.Same(sourceCode.Metadata, candidateCode.Metadata);
            Assert.Same(sourceCode.CodeText, candidateCode.CodeText);
            Assert.Equal(sourceCode.StableKey, candidateCode.StableKey);
            Assert.True(candidateCode.HasAppliedSyntaxHighlighting(highlightKey));
            Assert.Equal(sourceCode.PrivatePlacementForegroundSpanCount,
                candidateCode.PrivatePlacementForegroundSpanCount);
            Assert.Equal(sourceCode.CodeText, ConcatenateChunkText(candidateCode));
            Assert.Equal(GetCodeCopyAction(document.Snapshot).BlockIndex,
                GetCodeCopyAction(replacement).BlockIndex);
            Assert.Same(document.SourceMap, replacement.SourceMap);
            Assert.Equal(document.Snapshot.SemanticDocument.TextProjectionFingerprint,
                replacement.SemanticDocument.TextProjectionFingerprint);
            Assert.Equal(document.Snapshot.SemanticDocument.SemanticRangeFingerprint,
                replacement.SemanticDocument.SemanticRangeFingerprint);

            LazyLayoutCommit commit = replacement.EnsureMeasuredViewport(
                candidateCode.Bounds.Top,
                System.Math.Max(120, candidateCode.Bounds.Height),
                overscan: 0,
                CancellationToken.None);
            Assert.True(commit.Changed);
            Assert.True(replacement.IsTopLevelBlockMeasured(candidateCode));
            Assert.True(candidateCode.CopyButtonBounds.Width > 0);
            Assert.True(candidateCode.Bounds.Width > 0);
            Assert.True(candidateCode.Bounds.Height > 0);
            Assert.True(owner.HasCurrentSourceRaster());

            // Candidate realization owns new chunks and geometry; it must not
            // realize or rewrite the source's estimated code block.
            Assert.False(document.Snapshot.IsTopLevelBlockMeasured(sourceCode));
            Assert.Equal(sourceEstimatedBounds, sourceCode.Bounds);
            Assert.True(GetCodeCopyAction(replacement).IsCodeBlockCopy);
        }
        finally
        {
            await DisposeCandidateAndDrainAsync(candidate, candidateContext);
        }
    }

    [Fact]
    public async Task FinalCodePlacementFenceRejectsNewHighlightAndHorizontalScroll()
    {
        await using var document = await ParsedImageCodeDocument.CreateAsync(
            rasterizationScale: 1f, rightToLeft: false, lazy: false, spacerParagraphCount: 0);
        CodeBlockBox sourceCode = document.Code;
        CodeBlockHighlightCacheKey firstKey = CreateHighlight(sourceCode, providerRevision: 10);

        SingleRasterLayoutCandidate? firstCandidate = null;
        MarkdownLayoutContext? firstContext = null;
        SingleRasterLayoutCandidate? secondCandidate = null;
        MarkdownLayoutContext? secondContext = null;
        try
        {
            firstCandidate = CreateCandidate(document, out firstContext);
            SingleRasterLayoutCandidate firstOwner = Assert.IsType<SingleRasterLayoutCandidate>(firstCandidate);
            await firstOwner.DrainConstructionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(firstOwner.HasCurrentSourceRaster());

            CodeBlockHighlightCacheKey revisedKey = CreateHighlight(sourceCode, providerRevision: 11);
            Assert.NotEqual(firstKey, revisedKey);
            Assert.True(sourceCode.HasAppliedSyntaxHighlighting(revisedKey));
            Assert.False(firstOwner.HasCurrentSourceRaster());
            await DisposeCandidateAndDrainAsync(firstCandidate, firstContext);
            firstCandidate = null;
            firstContext = null;

            secondCandidate = CreateCandidate(document, out secondContext);
            SingleRasterLayoutCandidate secondOwner = Assert.IsType<SingleRasterLayoutCandidate>(secondCandidate);
            await secondOwner.DrainConstructionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(secondOwner.HasCurrentSourceRaster());
            Assert.True(sourceCode.CanScrollHorizontally);
            Assert.True(sourceCode.ScrollHorizontal(24));
            Assert.Equal(24, sourceCode.HorizontalOffset, precision: 1);
            Assert.False(secondOwner.HasCurrentSourceRaster());
        }
        finally
        {
            try
            {
                await DisposeCandidateAndDrainAsync(secondCandidate, secondContext);
            }
            finally
            {
                await DisposeCandidateAndDrainAsync(firstCandidate, firstContext);
            }
        }
    }

    private static SingleRasterLayoutCandidate? CreateCandidate(
        ParsedImageCodeDocument document,
        out MarkdownLayoutContext? candidateContext)
        => document.Snapshot.CreateSingleRasterCandidate(
            new[] { document.Image.BlockIndex },
            LayoutWidth,
            CancellationToken.None,
            CancellationToken.None,
            document.Seed.ImagePreparationOwnerToken,
            retirementSink: null,
            imageUnavailable: null,
            out candidateContext);

    private static CodeBlockHighlightCacheKey CreateHighlight(CodeBlockBox code, int providerRevision)
    {
        int publicStart = code.CodeText.IndexOf("public", StringComparison.Ordinal);
        int valueStart = code.CodeText.IndexOf("Value", StringComparison.Ordinal);
        Assert.True(publicStart >= 0);
        Assert.True(valueStart >= 0);

        var key = new CodeBlockHighlightCacheKey(
            code.CodeLanguage,
            code.Metadata.CodeTextHash,
            code.CodeText.Length,
            MarkdownThemeVariant.Light,
            ProviderIdentity: 417,
            ProviderRevision: providerRevision);
        CodeBlockHighlightSpan[] spans =
        [
            new(publicStart, "public".Length, Color.FromArgb(255, 173, 91, 219)),
            new(valueStart, "Value".Length, Color.FromArgb(255, 48, 132, 202)),
        ];
        Assert.True(code.ApplySyntaxHighlighting(key, spans));
        return key;
    }

    private static FocusableItem GetCodeCopyAction(LayoutSnapshot snapshot)
        => Assert.Single(snapshot.CollectFocusableItems(), static item => item.IsCodeBlockCopy);

    private static string ConcatenateChunkText(CodeBlockBox code)
        => string.Concat(code.Chunks.SelectMany(static chunk => chunk.Runs).Select(static run => run.Text));

    private static Rect[] GetRelativeChunkBounds(CodeBlockBox code)
        => code.Chunks.Select(chunk => RelativeTo(chunk.Bounds, code.Bounds)).ToArray();

    private static Color[] PaintCodeBlock(CanvasDevice device, CodeBlockBox code)
    {
        float width = System.Math.Max(1, (float)System.Math.Ceiling(code.Bounds.Width));
        float height = System.Math.Max(1, (float)System.Math.Ceiling(code.Bounds.Height));
        using var target = new CanvasRenderTarget(device, width, height,
            (float)(96 * code.Context.RasterizationScale));
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            drawing.Transform = Matrix3x2.CreateTranslation(
                (float)-code.Bounds.X,
                (float)-code.Bounds.Y);
            // Paint's viewport is in document coordinates; only the drawing
            // transform moves the source/candidate into the same target origin.
            code.Paint(drawing, code.Bounds);
        }
        return target.GetPixelColors();
    }

    private static Rect RelativeTo(Rect child, Rect owner)
        => new(child.X - owner.X, child.Y - owner.Y, child.Width, child.Height);

    private static async Task DisposeCandidateAndDrainAsync(
        SingleRasterLayoutCandidate? candidate,
        MarkdownLayoutContext? context)
    {
        try
        {
            candidate?.Dispose();
        }
        finally
        {
            if (context is not null)
                await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
