using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Markdig;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Windows.UI;
using Windows.Foundation;
using Xunit;
using Xunit.Abstractions;
using static MarkdownRenderer.GitHub.Tests.HtmlBreakImageSpacingFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks the measured geometry, source mapping and semantic projection of safe
/// HTML line breaks and images in GitHub README layouts.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because each test authors its own safe
/// HTML or Markdown source, and its boundary-control variants, next to the
/// geometry, source-map and semantic checks for it; the shared builders and break
/// assertions live in <see cref="HtmlBreakImageSpacingFixture"/>.
/// </remarks>
public sealed class HtmlBreakImageSpacingTests(ITestOutputHelper output)
{
    [Fact]
    public void StandaloneHtmlBreakAndFollowingMarkdownImageExposeTheirMeasuredBlockGeometry()
    {
        const string source = "<br>\n\n![](https://example.test/divider.png)\n\nAfter the divider.";
        using LayoutSnapshot snapshot = BuildGitHub(source);

        Assert.Equal(3, snapshot.Blocks.Count);
        StackBox htmlBlock = Assert.IsType<StackBox>(snapshot.Blocks[0]);
        InlineContainerBox breakBox = Assert.IsType<InlineContainerBox>(Assert.Single(htmlBlock.Children));
        LineBreakRun lineBreak = Assert.IsType<LineBreakRun>(Assert.Single(breakBox.Runs));
        ImageBox image = Assert.IsType<ImageBox>(snapshot.Blocks[1]);
        InlineContainerBox followingParagraph = Assert.IsType<InlineContainerBox>(snapshot.Blocks[2]);

        float breakContentHeight = (float)(breakBox.Bounds.Height - breakBox.Margin.Top - breakBox.Margin.Bottom);
        float contentGap = (float)(image.Bounds.Top + image.Margin.Top -
            (breakBox.Bounds.Bottom - breakBox.Margin.Bottom));
        var breakRange = new DocumentRange(
            new DocumentPosition(breakBox.BlockIndex, lineBreak.InlineIndex, 0),
            new DocumentPosition(breakBox.BlockIndex, lineBreak.InlineIndex, lineBreak.Text.Length));
        int semanticStart = snapshot.SemanticDocument.TextOffsetFromDocumentPosition(breakRange.Start);
        int semanticEnd = snapshot.SemanticDocument.TextOffsetFromDocumentPosition(breakRange.End);
        Rect selectionRect = Assert.Single(breakBox.GetSelectionRects(breakRange));

        output.WriteLine($"break bounds: {breakBox.Bounds}; margin: {breakBox.Margin}; content-height: {breakContentHeight}");
        output.WriteLine($"HTML block bounds: {htmlBlock.Bounds}; image bounds: {image.Bounds}; image margin: {image.Margin}");
        output.WriteLine($"image measured content: {image.MeasuredImageWidth} x {image.MeasuredImageHeight}; break-content-to-image-content gap: {contentGap}");
        output.WriteLine($"following paragraph bounds: {followingParagraph.Bounds}");
        output.WriteLine($"semantic break text: '{snapshot.SemanticDocument.Text.Substring(semanticStart, semanticEnd - semanticStart)}'; selection rect: {selectionRect}");

        Assert.Equal(default, breakBox.Margin);
        Assert.True(lineBreak.UseSingleLineLayout);
        Assert.Equal("\n", lineBreak.Text);
        Assert.Equal("\n", lineBreak.AccessibleText);
        Assert.Equal(" ", lineBreak.LayoutText);
        Assert.Equal("\n", snapshot.SemanticDocument.Text.Substring(semanticStart, semanticEnd - semanticStart));
        Assert.DoesNotContain('\u200B', snapshot.SemanticDocument.Text);
        Assert.Equal("<br>", snapshot.SourceMap.Slice(breakRange));
        Assert.Equal(24, breakContentHeight, precision: 2);
        Assert.Equal(24, breakBox.Bounds.Height, precision: 2);
        Assert.Equal(24, selectionRect.Height, precision: 2);
        Assert.True(selectionRect.Width > 0);
        Assert.Equal(new Thickness(0, 0, 0, 16), image.Margin);
        Assert.Equal(0, contentGap, precision: 2);
        Assert.Equal(32, image.MeasuredImageHeight);
        Assert.Equal(48, image.Bounds.Height);
        Assert.Equal(image.Bounds.Top, htmlBlock.Bounds.Bottom, precision: 2);
        Assert.Equal(image.Bounds.Bottom, followingParagraph.Bounds.Top, precision: 2);
    }

    [Fact]
    public void BreakOnlySafeHtmlContainerUsesOneLineBoxWithoutBodyParagraphMargin()
    {
        const string source = "<div><br></div>\n\nAfter.";
        using LayoutSnapshot snapshot = BuildGitHub(source);

        StackBox htmlBlock = Assert.IsType<StackBox>(snapshot.Blocks[0]);
        InlineContainerBox breakBox = Assert.IsType<InlineContainerBox>(Assert.Single(htmlBlock.Children));
        LineBreakRun lineBreak = Assert.IsType<LineBreakRun>(Assert.Single(breakBox.Runs));

        Assert.Equal(default, breakBox.Margin);
        Assert.True(lineBreak.UseSingleLineLayout);
        Assert.Equal("\n", lineBreak.Text);
        Assert.Equal(24, breakBox.Bounds.Height, precision: 2);
        Assert.Equal("<br>", snapshot.SourceMap.Slice(new DocumentRange(
            new DocumentPosition(breakBox.BlockIndex, lineBreak.InlineIndex, 0),
            new DocumentPosition(breakBox.BlockIndex, lineBreak.InlineIndex, 1))));
    }

    [Fact]
    public void BreakOnlySafeHtmlParagraphRetainsItsParagraphMargin()
    {
        const string source = "<p><br></p>\n\nAfter.";
        using LayoutSnapshot snapshot = BuildGitHub(source);

        StackBox htmlBlock = Assert.IsType<StackBox>(snapshot.Blocks[0]);
        InlineContainerBox breakBox = Assert.IsType<InlineContainerBox>(Assert.Single(htmlBlock.Children));
        LineBreakRun lineBreak = Assert.IsType<LineBreakRun>(Assert.Single(breakBox.Runs));

        Assert.True(lineBreak.UseSingleLineLayout);
        Assert.Equal("\n", lineBreak.Text);
        Assert.Equal(new Thickness(0, 0, 0, 16), breakBox.Margin);
        Assert.Equal(40, breakBox.Bounds.Height, precision: 2);
        Assert.Equal("<br>", snapshot.SourceMap.Slice(new DocumentRange(
            new DocumentPosition(breakBox.BlockIndex, lineBreak.InlineIndex, 0),
            new DocumentPosition(breakBox.BlockIndex, lineBreak.InlineIndex, 1))));
    }

    [Fact]
    public void AnonymousInlineFragmentsInSafeHtmlContainersDoNotAcquireParagraphMargins()
    {
        const string source = """
            <div>
            <img src="https://example.test/logo.png" width="40" height="20" alt="logo"><br><br><br><br>
            <div><span>Nested caption</span></div>
            Before section<br>
            <section><span>Section content</span></section>
            Tail fragment
            <p>Explicit paragraph<br>Paragraph tail</p>
            Final tail fragment
            </div>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        StackBox htmlBlock = Assert.IsType<StackBox>(Assert.Single(snapshot.Blocks));
        StackBox outerContainer = Assert.IsType<StackBox>(Assert.Single(htmlBlock.Children));
        InlineContainerBox[] inlineChildren = outerContainer.Children.OfType<InlineContainerBox>().ToArray();

        InlineContainerBox imageAndBreaks = Assert.Single(
            inlineChildren,
            static box => box.Runs.OfType<InlineImageRun>().Any());
        InlineContainerBox nestedCaption = Assert.Single(
            inlineChildren,
            static box => box.Runs.Any(static run => run.Text.Contains("Nested caption", StringComparison.Ordinal)));
        InlineContainerBox beforeSection = Assert.Single(
            inlineChildren,
            static box => box.Runs.Any(static run => run.Text.Contains("Before section", StringComparison.Ordinal)));
        InlineContainerBox sectionContent = Assert.Single(
            inlineChildren,
            static box => box.Runs.Any(static run => run.Text.Contains("Section content", StringComparison.Ordinal)));
        InlineContainerBox tail = Assert.Single(
            inlineChildren,
            static box => box.Runs.Any(static run => run.Text.Contains("Tail fragment", StringComparison.Ordinal)));
        InlineContainerBox paragraph = Assert.Single(
            inlineChildren,
            static box => box.Runs.Any(static run => run.Text.Contains("Explicit paragraph", StringComparison.Ordinal)));
        InlineContainerBox finalTail = Assert.Single(
            inlineChildren,
            static box => box.Runs.Any(static run => run.Text.Contains("Final tail fragment", StringComparison.Ordinal)));

        Assert.Equal(default, imageAndBreaks.Margin);
        Assert.Equal(default, nestedCaption.Margin);
        Assert.Equal(default, beforeSection.Margin);
        Assert.Equal(default, sectionContent.Margin);
        Assert.Equal(default, tail.Margin);
        Assert.Equal(new Thickness(0, 0, 0, 16), paragraph.Margin);
        Assert.Equal(default, finalTail.Margin);

        Assert.Equal(4, imageAndBreaks.Runs.OfType<LineBreakRun>().Count());
        Assert.Single(paragraph.Runs.OfType<LineBreakRun>());
        LineBreakRun[] allBreaks = inlineChildren
            .SelectMany(static box => box.Runs.OfType<LineBreakRun>())
            .ToArray();
        Assert.Equal(6, allBreaks.Length);
        foreach (InlineContainerBox box in inlineChildren)
        {
            foreach (LineBreakRun lineBreak in box.Runs.OfType<LineBreakRun>())
            {
                var range = new DocumentRange(
                    new DocumentPosition(box.BlockIndex, lineBreak.InlineIndex, 0),
                    new DocumentPosition(box.BlockIndex, lineBreak.InlineIndex, lineBreak.Text.Length));
                Assert.Equal("<br>", snapshot.SourceMap.Slice(range));
                int semanticStart = snapshot.SemanticDocument.TextOffsetFromDocumentPosition(range.Start);
                int semanticEnd = snapshot.SemanticDocument.TextOffsetFromDocumentPosition(range.End);
                Assert.Equal("\n", snapshot.SemanticDocument.Text.Substring(
                    semanticStart,
                    semanticEnd - semanticStart));
                Assert.NotEmpty(box.GetSelectionRects(range));
            }
        }
    }

    [Fact]
    public void MixedInlineFragmentsUseBlockBoundaryForTerminalBreakLayoutOnly()
    {
        const string source = "<div align=\"center\"><img src=\"https://example.test/wordmark.png\" width=\"500\" height=\"350\" alt=\"wordmark\"><br><br><br><br><div><sub>Check out my macOS app</sub><br><h2><a href=\"https://example.test/app\">Supercharge</a><br><sup>Elevate your Mac experience</sup></h2></div><br><br><br><br><hr></div>";

        foreach (MarkdownStyleSheet? styleSheet in new MarkdownStyleSheet?[]
        {
            null,
            GitHubReadmeVisualStyleProfiles.PrimerLightV5.StyleSheet,
        })
        {
            using LayoutSnapshot snapshot = BuildGitHub(source, styleSheet: styleSheet);
            using LayoutSnapshot imageBoundaryControl = BuildGitHub(
                source.Replace("<br><br><br><br><div>", "<br><br><br>&nbsp;<div>", StringComparison.Ordinal),
                styleSheet: styleSheet);
            using LayoutSnapshot headingBoundaryControl = BuildGitHub(
                source.Replace("</sub><br><h2>", "</sub>&nbsp;<h2>", StringComparison.Ordinal),
                styleSheet: styleSheet);
            using LayoutSnapshot ruleBoundaryControl = BuildGitHub(
                source.Replace("</h2></div><br><br><br><br><hr>", "</h2></div><br><br><br>&nbsp;<hr>", StringComparison.Ordinal),
                styleSheet: styleSheet);
            InlineContainerBox[] inlineBoxes = EnumerateInlineBoxes(snapshot.Blocks).ToArray();
            InlineContainerBox imageAndBreaks = Assert.Single(inlineBoxes, static box =>
                box.Runs.OfType<InlineImageRun>().Any() &&
                box.Runs.OfType<LineBreakRun>().Count() == 4);
            InlineContainerBox tagline = Assert.Single(inlineBoxes, static box =>
                box.Runs.Any(static run => run.Text.Contains("Check out my macOS app", StringComparison.Ordinal)));
            InlineContainerBox heading = Assert.Single(inlineBoxes, static box =>
                box.Runs.OfType<LinkRun>().Any());
            InlineContainerBox breaksBeforeRule = Assert.Single(inlineBoxes, static box =>
                !box.Runs.OfType<InlineImageRun>().Any() &&
                box.Runs.OfType<LineBreakRun>().Count() == 4);
            InlineContainerBox expectedImageFragment = Assert.Single(
                EnumerateInlineBoxes(imageBoundaryControl.Blocks),
                static box => box.Runs.OfType<InlineImageRun>().Any());
            InlineContainerBox expectedTagline = Assert.Single(
                EnumerateInlineBoxes(headingBoundaryControl.Blocks),
                static box => box.Runs.Any(static run => run.Text.Contains("Check out my macOS app", StringComparison.Ordinal)));
            InlineContainerBox expectedBeforeRule = Assert.Single(
                EnumerateInlineBoxes(ruleBoundaryControl.Blocks),
                static box => box.Runs.OfType<LineBreakRun>().Count() == 3);

            LineBreakRun[] imageBreaks = imageAndBreaks.Runs.OfType<LineBreakRun>().ToArray();
            LineBreakRun taglineBreak = Assert.Single(tagline.Runs.OfType<LineBreakRun>());
            LineBreakRun headingLineBreak = Assert.Single(heading.Runs.OfType<LineBreakRun>());
            LineBreakRun[] ruleBreaks = breaksBeforeRule.Runs.OfType<LineBreakRun>().ToArray();

            output.WriteLine($"style sheet: {(styleSheet is null ? "default" : "PrimerLightV5")}");
            output.WriteLine($"image fragment: {imageAndBreaks.Bounds}; image height {Assert.Single(imageAndBreaks.Runs.OfType<InlineImageRun>()).DesiredHeight}");
            output.WriteLine($"tagline fragment: {tagline.Bounds}; heading: {heading.Bounds}; heading margin: {heading.Margin}");
            output.WriteLine($"rule break fragment: {breaksBeforeRule.Bounds}");
            output.WriteLine($"boundary controls: image {expectedImageFragment.Bounds}; tagline {expectedTagline.Bounds}; rule {expectedBeforeRule.Bounds}");

            Assert.Equal(expectedImageFragment.Bounds.Height, imageAndBreaks.Bounds.Height, precision: 2);
            Assert.Equal(expectedTagline.Bounds.Height, tagline.Bounds.Height, precision: 2);
            Assert.Equal(expectedBeforeRule.Bounds.Height, breaksBeforeRule.Bounds.Height, precision: 2);

            AssertTerminalBreakLayout(imageBreaks, taglineBreak, headingLineBreak, ruleBreaks);

            AssertBreaksMapToSourceAndSelection(snapshot, imageAndBreaks, tagline, heading, breaksBeforeRule);

            // Keep the exact no-footer source above for geometry, but append a
            // semantic paragraph here as a nonterminal control. The authored
            // breaks must map to newlines both here and before a textless <hr>.
            using LayoutSnapshot semanticProjectionControl = BuildGitHub(
                source + "\n\nAfter the rule.",
                styleSheet: styleSheet);
            AssertBreaksProjectToSemanticNewlines(semanticProjectionControl);
        }
    }

    [Fact]
    public void TrailingBreakBeforeTextlessRuleRetainsRunAndSourceWhenProjectionTrimsFinalNewline()
    {
        // Authored content must survive trimming of the final newline. Only
        // generated block separators may be trimmed.
        const string source = "<div>Tail<br><hr></div>";
        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox tail = Assert.Single(EnumerateInlineBoxes(snapshot.Blocks), static box =>
            box.Runs.Any(static run => run.Text.Contains("Tail", StringComparison.Ordinal)));
        LineBreakRun lineBreak = Assert.Single(tail.Runs.OfType<LineBreakRun>());
        var range = new DocumentRange(
            new DocumentPosition(tail.BlockIndex, lineBreak.InlineIndex, 0),
            new DocumentPosition(tail.BlockIndex, lineBreak.InlineIndex, lineBreak.Text.Length));

        Assert.Equal("\n", lineBreak.AccessibleText);
        Assert.True(lineBreak.UseSingleLineLayout);
        Assert.Equal(" ", lineBreak.LayoutText);
        Assert.Equal("Tail\n", snapshot.SemanticDocument.Text);
        Assert.Equal("<br>", snapshot.SourceMap.Slice(range));
        Assert.NotEmpty(tail.GetSelectionRects(range));
    }

    [Theory]
    [InlineData("<div>Tail<br><hr></div>")]
    [InlineData("<div>Tail<br><br><hr></div>")]
    [InlineData("<div dir=\"rtl\">שלום 😀<br><br><hr></div>")]
    public void TrimmedTerminalBreaksKeepAllPublishedSemanticRangesWithinText(string source)
    {
        using LayoutSnapshot snapshot = BuildGitHub(source);
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        InlineContainerBox[] boxes = EnumerateInlineBoxes(snapshot.Blocks).ToArray();
        LineBreakRun[] lineBreaks = boxes.SelectMany(static box => box.Runs.OfType<LineBreakRun>()).ToArray();
        string expectedText = source switch
        {
            "<div>Tail<br><hr></div>" => "Tail\n",
            "<div>Tail<br><br><hr></div>" => "Tail\n\n",
            "<div dir=\"rtl\">שלום 😀<br><br><hr></div>" => "שלום 😀\n\n",
            _ => throw new InvalidOperationException("The terminal-break fixture needs an independent text oracle."),
        };

        Assert.NotEmpty(lineBreaks);
        AssertSemanticRangesWithinText(semantic);
        Assert.Equal(expectedText, semantic.Text);
        int expectedBreakOffset = expectedText.IndexOf('\n');

        foreach (InlineContainerBox box in boxes)
        {
            foreach (LineBreakRun lineBreak in box.Runs.OfType<LineBreakRun>())
            {
                MarkdownTextSpan span = Assert.Single(
                    semantic.TextSpans,
                    candidate => ReferenceEquals(candidate.InlineBox, box) &&
                        ReferenceEquals(candidate.InlineRun, lineBreak));
                var range = new DocumentRange(
                    new DocumentPosition(box.BlockIndex, lineBreak.InlineIndex, 0),
                    new DocumentPosition(box.BlockIndex, lineBreak.InlineIndex, lineBreak.Text.Length));

                Assert.Equal(expectedBreakOffset, span.TextStart);
                Assert.Equal(expectedBreakOffset + 1, span.TextEnd);
                Assert.Equal("\n", semantic.Text.Substring(span.TextStart, span.TextEnd - span.TextStart));
                Assert.Equal(expectedBreakOffset, semantic.TextOffsetFromDocumentPosition(range.Start));
                Assert.Equal(expectedBreakOffset + 1, semantic.TextOffsetFromDocumentPosition(range.End));
                Assert.Equal("<br>", snapshot.SourceMap.Slice(range));
                Assert.NotEmpty(box.GetSelectionRects(range));
                expectedBreakOffset++;
            }
        }
        Assert.Equal(expectedText.Length, expectedBreakOffset);
    }

    [Fact]
    public void EmptySemanticDocumentPublishesOnlyZeroLengthRanges()
    {
        using LayoutSnapshot snapshot = BuildGitHub(string.Empty);
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;

        Assert.Equal(string.Empty, semantic.Text);
        AssertSemanticRangesWithinText(semantic);
        Assert.Equal(0, semantic.Root.TextStart);
        Assert.Equal(0, semantic.Root.TextEnd);
    }

    [Fact]
    public void AppendedFooterPreservesRtlEmojiBreakTextAndNormalizedRanges()
    {
        const string source = "<div dir=\"rtl\">שלום 😀<br><br><hr></div>\n\nAfter the rule.";
        using LayoutSnapshot snapshot = BuildGitHub(source);
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        AssertSemanticRangesWithinText(semantic);

        foreach (InlineContainerBox box in EnumerateInlineBoxes(snapshot.Blocks))
        {
            foreach (LineBreakRun lineBreak in box.Runs.OfType<LineBreakRun>())
            {
                MarkdownTextSpan span = Assert.Single(
                    semantic.TextSpans,
                    candidate => ReferenceEquals(candidate.InlineBox, box) &&
                        ReferenceEquals(candidate.InlineRun, lineBreak));

                Assert.Equal("\n", semantic.Text.Substring(span.TextStart, span.TextEnd - span.TextStart));
                Assert.Equal(span.TextStart,
                    semantic.TextOffsetFromDocumentPosition(
                        new DocumentPosition(box.BlockIndex, lineBreak.InlineIndex, 0)));
                Assert.Equal(span.TextEnd,
                    semantic.TextOffsetFromDocumentPosition(
                        new DocumentPosition(box.BlockIndex, lineBreak.InlineIndex, lineBreak.Text.Length)));
            }
        }
    }

    [Fact]
    public void MixedTerminalBreakAtContainerEndKeepsItsExplicitTrailingLine()
    {
        const string source = "<div>Tail<br><br></div>";
        using LayoutSnapshot snapshot = BuildGitHub(source);

        InlineContainerBox tail = Assert.Single(EnumerateInlineBoxes(snapshot.Blocks), static box =>
            box.Runs.Any(static run => run.Text.Contains("Tail", StringComparison.Ordinal)));
        LineBreakRun[] breaks = tail.Runs.OfType<LineBreakRun>().ToArray();

        Assert.Equal(2, breaks.Length);
        Assert.All(breaks, static lineBreak => Assert.False(lineBreak.UseSingleLineLayout));
        Assert.Equal("Tail\n\n", tail.Runs.Select(static run => run.Text).Aggregate(string.Concat));
        foreach (LineBreakRun lineBreak in breaks)
        {
            var range = new DocumentRange(
                new DocumentPosition(tail.BlockIndex, lineBreak.InlineIndex, 0),
                new DocumentPosition(tail.BlockIndex, lineBreak.InlineIndex, lineBreak.Text.Length));
            Assert.Equal("<br>", snapshot.SourceMap.Slice(range));
        }
    }

    [Fact]
    public void RightToLeftMixedFragmentNormalizesOnlyItsTerminalBreakBeforeBlock()
    {
        const string source = "<div dir=\"rtl\">שלום<br><div>הבא</div></div>";
        using LayoutSnapshot snapshot = BuildGitHub(
            source,
            flowDirection: FlowDirection.RightToLeft);

        InlineContainerBox beforeBlock = Assert.Single(EnumerateInlineBoxes(snapshot.Blocks), static box =>
            box.Runs.Any(static run => run.Text.Contains("שלום", StringComparison.Ordinal)));
        LineBreakRun lineBreak = Assert.Single(beforeBlock.Runs.OfType<LineBreakRun>());

        Assert.True(lineBreak.UseSingleLineLayout);
        Assert.Equal("\n", lineBreak.Text);
        Assert.Equal(" ", lineBreak.LayoutText);
        var range = new DocumentRange(
            new DocumentPosition(beforeBlock.BlockIndex, lineBreak.InlineIndex, 0),
            new DocumentPosition(beforeBlock.BlockIndex, lineBreak.InlineIndex, lineBreak.Text.Length));
        Assert.Equal("\n", snapshot.SemanticDocument.Text.Substring(
            snapshot.SemanticDocument.TextOffsetFromDocumentPosition(range.Start),
            snapshot.SemanticDocument.TextOffsetFromDocumentPosition(range.End) -
                snapshot.SemanticDocument.TextOffsetFromDocumentPosition(range.Start)));
        Assert.Equal("<br>", snapshot.SourceMap.Slice(range));
    }

    [Fact]
    public void MarkdownReparsedSafeHtmlContainerRetainsItsRealParagraphMargin()
    {
        const string source = """
            <div>
            Reparsed markdown paragraph
            </div>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        StackBox htmlBlock = Assert.IsType<StackBox>(Assert.Single(snapshot.Blocks));
        StackBox markdownContainer = Assert.IsType<StackBox>(Assert.Single(htmlBlock.Children));
        InlineContainerBox paragraph = Assert.IsType<InlineContainerBox>(Assert.Single(markdownContainer.Children));

        Assert.Contains(paragraph.Runs, static run =>
            run.Text.Contains("Reparsed markdown paragraph", StringComparison.Ordinal));
        Assert.Equal(new Thickness(0, 0, 0, 16), paragraph.Margin);
    }

    [Fact]
    public void MarkdownImageOnlyParagraphIsPromotedWithBodyParagraphMargins()
    {
        const string source = "![](https://example.test/divider.png)\n\nFollowing paragraph.";
        using LayoutSnapshot snapshot = BuildGitHub(source);

        ImageBox image = Assert.IsType<ImageBox>(snapshot.Blocks[0]);
        InlineContainerBox followingParagraph = Assert.IsType<InlineContainerBox>(snapshot.Blocks[1]);

        output.WriteLine($"image-only paragraph bounds: {image.Bounds}; margin: {image.Margin}; measured content-height: {image.MeasuredImageHeight}");
        output.WriteLine($"following paragraph bounds: {followingParagraph.Bounds}; inter-block gap: {followingParagraph.Bounds.Top - image.Bounds.Bottom}");

        Assert.Equal(new Thickness(0, 0, 0, 16), image.Margin);
        Assert.Equal(32, image.MeasuredImageHeight);
        Assert.Equal(48, image.Bounds.Height);
        Assert.Equal(image.Bounds.Bottom, followingParagraph.Bounds.Top, precision: 2);
    }

    [Fact]
    public void SafeHtmlImageParagraphProvidesReferenceForMarkdownImagePromotionGeometry()
    {
        const string source = "<p><img src=\"https://example.test/divider.png\" alt=\"\"></p>\n\nFollowing paragraph.";
        using LayoutSnapshot snapshot = BuildGitHub(source);

        StackBox htmlBlock = Assert.IsType<StackBox>(snapshot.Blocks[0]);
        InlineContainerBox paragraph = Assert.IsType<InlineContainerBox>(Assert.Single(htmlBlock.Children));
        InlineImageRun imageRun = Assert.IsType<InlineImageRun>(Assert.Single(paragraph.Runs));
        InlineContainerBox followingParagraph = Assert.IsType<InlineContainerBox>(snapshot.Blocks[1]);

        output.WriteLine($"safe HTML image paragraph bounds: {paragraph.Bounds}; margin: {paragraph.Margin}");
        output.WriteLine($"HTML inline image box bounds: {imageRun.Image.Bounds}; image margin: {imageRun.Image.Margin}; measured content-height: {imageRun.DesiredHeight}");
        output.WriteLine($"following paragraph top: {followingParagraph.Bounds.Top}; HTML paragraph bottom: {paragraph.Bounds.Bottom}");

        Assert.Equal(new Thickness(0, 0, 0, 16), paragraph.Margin);
        Assert.Equal(default, imageRun.Image.Margin);
        Assert.Equal(paragraph.Bounds.Bottom, followingParagraph.Bounds.Top, precision: 2);
    }

    [Fact]
    public async Task LoadedMarkdownAndHtmlImagesMatchBrowserParagraphFlowGeometry()
    {
        const string markdownUrl = "https://example.test/markdown-loaded.png";
        const string htmlUrl = "https://example.test/html-loaded.png";
        byte[] png = CreatePng(width: 160, height: 80);
        Assert.Equal(
            "7a658bc11f15e70a1e500525dfdb86a4eaebe1ed1c5b0157e0888f32094d9bf0",
            Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant());
        var resolver = new DeterministicPngResolver(png);
        using LayoutSnapshot markdownSnapshot = BuildGitHub(
            $"![]({markdownUrl})\n\nFollowing paragraph.",
            resolver);
        using LayoutSnapshot htmlSnapshot = BuildGitHub(
            $"<p><img src=\"{htmlUrl}\" alt=\"\"></p>\n\nFollowing paragraph.",
            resolver);

        ImageBox markdownImage = Assert.IsType<ImageBox>(markdownSnapshot.Blocks[0]);
        InlineContainerBox markdownFollowingParagraph = Assert.IsType<InlineContainerBox>(markdownSnapshot.Blocks[1]);
        StackBox htmlBlock = Assert.IsType<StackBox>(htmlSnapshot.Blocks[0]);
        InlineContainerBox htmlParagraph = Assert.IsType<InlineContainerBox>(Assert.Single(htmlBlock.Children));
        InlineImageRun htmlImageRun = Assert.IsType<InlineImageRun>(Assert.Single(htmlParagraph.Runs));
        InlineContainerBox htmlFollowingParagraph = Assert.IsType<InlineContainerBox>(htmlSnapshot.Blocks[1]);

        await WaitForImageLoadAsync(markdownImage);
        await WaitForImageLoadAsync(htmlImageRun.Image);
        Assert.NotNull(markdownImage.Bitmap);
        Assert.NotNull(htmlImageRun.Image.Bitmap);

        markdownSnapshot.RelayoutMeasuredBlocks(660, CancellationToken.None);
        htmlSnapshot.RelayoutMeasuredBlocks(660, CancellationToken.None);

        output.WriteLine($"loaded markdown block: {markdownImage.Bounds}; content: {markdownImage.MeasuredImageWidth} x {markdownImage.MeasuredImageHeight}; margin: {markdownImage.Margin}");
        output.WriteLine($"loaded HTML paragraph: {htmlParagraph.Bounds}; image: {htmlImageRun.DesiredWidth} x {htmlImageRun.DesiredHeight}; margin: {htmlParagraph.Margin}");

        Assert.Equal(160, markdownImage.MeasuredImageWidth);
        Assert.Equal(80, markdownImage.MeasuredImageHeight);
        Assert.Equal(160, htmlImageRun.DesiredWidth);
        Assert.Equal(80, htmlImageRun.DesiredHeight);
        Assert.Equal(new Thickness(0, 0, 0, 16), markdownImage.Margin);
        Assert.Equal(new Thickness(0, 0, 0, 16), htmlParagraph.Margin);
        Assert.Equal(96, markdownImage.Bounds.Height, precision: 2);
        Assert.Equal(96, htmlParagraph.Bounds.Height, precision: 2);
        Assert.Equal(markdownImage.Bounds.Bottom, markdownFollowingParagraph.Bounds.Top, precision: 2);
        Assert.Equal(htmlParagraph.Bounds.Bottom, htmlFollowingParagraph.Bounds.Top, precision: 2);
    }

    [Fact]
    public async Task TinyMarkdownImageRetainsTheSameParagraphLineBoxAsHtml()
    {
        const string markdownUrl = "https://example.test/tiny-markdown.png";
        const string htmlUrl = "https://example.test/tiny-html.png";
        var resolver = new DeterministicPngResolver(CreatePng(width: 160, height: 5));
        using LayoutSnapshot markdownSnapshot = BuildGitHub(
            $"![]({markdownUrl})\n\nFollowing paragraph.",
            resolver);
        using LayoutSnapshot htmlSnapshot = BuildGitHub(
            $"<p><img src=\"{htmlUrl}\" alt=\"\"></p>\n\nFollowing paragraph.",
            resolver);

        ImageBox markdownImage = Assert.IsType<ImageBox>(markdownSnapshot.Blocks[0]);
        StackBox htmlBlock = Assert.IsType<StackBox>(htmlSnapshot.Blocks[0]);
        InlineContainerBox htmlParagraph = Assert.IsType<InlineContainerBox>(Assert.Single(htmlBlock.Children));
        InlineImageRun htmlImage = Assert.IsType<InlineImageRun>(Assert.Single(htmlParagraph.Runs));

        await WaitForImageLoadAsync(markdownImage);
        await WaitForImageLoadAsync(htmlImage.Image);
        markdownSnapshot.RelayoutMeasuredBlocks(660, CancellationToken.None);
        htmlSnapshot.RelayoutMeasuredBlocks(660, CancellationToken.None);

        Assert.Equal(5, markdownImage.MeasuredImageHeight);
        Assert.Equal(24, htmlParagraph.Bounds.Height - htmlParagraph.Margin.Bottom, precision: 2);
        Assert.Equal(htmlParagraph.Bounds.Height, markdownImage.Bounds.Height, precision: 2);
        Rect htmlImageRect = Assert.Single(htmlParagraph.EnumerateInlineImageRects()).Rect;
        Rect markdownImageRect = markdownImage.AutomationRenderDestination;
        Assert.Equal(htmlImageRect.Y, markdownImageRect.Y, precision: 2);
        Assert.Equal(htmlImageRect.Height, markdownImageRect.Height, precision: 2);
    }
}
