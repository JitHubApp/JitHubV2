using MarkdownRenderer.Document;
using MarkdownRenderer.Html;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.SafeHtmlRenderingFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks source-less decorative spacer images: their admission policy,
/// authored geometry, semantic mapping and table min-content effect.
/// </summary>
public sealed class SafeHtmlDecorativeSpacerTests
{
    [Fact]
    public void ExplicitlySizedSourceLessEmptyAltImageUsesInvisibleMappedSpacer()
    {
        const string source = "before<img width='16' height='1' alt=''>after";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineSpacerRun spacer = Assert.Single(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
        InlineContainerBox owner = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            box => box.Runs.Contains(spacer));

        Assert.Equal(16f, spacer.Width);
        Assert.Equal(1f, spacer.Height);
        Assert.Equal(1, spacer.TextLength);
        Assert.Equal("\uFFFC", spacer.Text);
        Assert.Empty(spacer.AccessibleText);
        Assert.True(PrivateTextRunPlacement.IsSupported(spacer));
        InlineSpacerRun clone = Assert.IsType<InlineSpacerRun>(PrivateTextRunPlacement.Clone(spacer));
        Assert.NotSame(spacer, clone);
        Assert.Equal(spacer.Width, clone.Width);
        Assert.Equal(spacer.Height, clone.Height);
        Assert.Equal(spacer.SourceSpan, clone.SourceSpan);

        Rect spacerBounds = owner.GetRunRect(spacer.InlineIndex);
        Assert.InRange(spacerBounds.Width, 15.5, 16.5);
        Assert.DoesNotContain("\uFFFC", snapshot.SemanticDocument.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            snapshot.SemanticDocument.TextSpans,
            span => ReferenceEquals(span.InlineRun, spacer));
        Assert.Contains("before", snapshot.SemanticDocument.Text, StringComparison.Ordinal);
        Assert.Contains("after", snapshot.SemanticDocument.Text, StringComparison.Ordinal);
        int selectionStart = snapshot.SemanticDocument.TextOffsetFromDocumentPosition(
            new MarkdownRenderer.Document.DocumentPosition(owner.BlockIndex, spacer.InlineIndex, 0));
        int selectionEnd = snapshot.SemanticDocument.TextOffsetFromDocumentPosition(
            new MarkdownRenderer.Document.DocumentPosition(owner.BlockIndex, spacer.InlineIndex, spacer.TextLength));
        Assert.Equal(selectionStart, selectionEnd);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.75f)]
    public void DecorativeSpacerPreservesFractionalAuthoredGeometryAcrossTextScaling(float textScaleFactor)
    {
        const string source = "before<img width='16.25' height='1.5' alt=''>after";

        using LayoutSnapshot snapshot = Build(
            source,
            SafeHtmlOptions.Default,
            textScaleFactor: textScaleFactor);
        InlineSpacerRun spacer = Assert.Single(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
        InlineContainerBox owner = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            box => box.Runs.Contains(spacer));
        Rect bounds = owner.GetRunRect(spacer.InlineIndex);

        Assert.Equal(16.25f, spacer.Width);
        Assert.Equal(1.5f, spacer.Height);
        Assert.InRange(bounds.Width, 16.2, 16.3);
        Assert.DoesNotContain("\uFFFC", snapshot.SemanticDocument.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("before<img width='16' height='1' alt=''>after", "after", 0)]
    [InlineData("<img width='16' height='1' alt=''>after", "after", 0)]
    [InlineData("before<img width='16' height='1' alt=''>", "before", 6)]
    [InlineData("before<strong>middle</strong><img width='16' height='1' alt=''>", "middle", 6)]
    public void DecorativeSpacerEndpointsMapToAdjacentSemanticBoundaryAndRoundTrip(
        string source,
        string boundaryText,
        int boundaryOffset)
    {
        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineSpacerRun spacer = Assert.Single(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
        InlineContainerBox owner = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            box => box.Runs.Contains(spacer));
        MarkdownRenderer.Accessibility.MarkdownSemanticDocument semantics = snapshot.SemanticDocument;
        int boundaryStart = semantics.Text.IndexOf(boundaryText, StringComparison.Ordinal);
        Assert.True(boundaryStart >= 0);
        int expected = boundaryStart + boundaryOffset;
        DocumentPosition start = new(owner.BlockIndex, spacer.InlineIndex, 0);
        DocumentPosition end = new(owner.BlockIndex, spacer.InlineIndex, spacer.TextLength);

        Assert.Equal(expected, semantics.TextOffsetFromDocumentPosition(start));
        Assert.Equal(expected, semantics.TextOffsetFromDocumentPosition(end));
        Assert.DoesNotContain("\uFFFC", semantics.Text, StringComparison.Ordinal);

        DocumentPosition? roundTrip = semantics.PositionFromTextOffset(expected);
        Assert.NotNull(roundTrip);
        Assert.Equal(expected, semantics.TextOffsetFromDocumentPosition(roundTrip.Value));
    }

    [Fact]
    public void SpacerOnlyParagraphMapsToItsZeroLengthSemanticNode()
    {
        const string source = "first paragraph\n\n<img width='16' height='1' alt=''>\n\nlast paragraph";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineSpacerRun spacer = Assert.Single(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
        InlineContainerBox owner = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            box => box.Runs.Contains(spacer));
        MarkdownRenderer.Accessibility.MarkdownSemanticDocument semantics = snapshot.SemanticDocument;
        Assert.True(semantics.TryGetInlineContainerNode(owner, out var semanticNode));
        Assert.Equal(semanticNode.TextStart, semanticNode.TextEnd);

        DocumentPosition start = new(owner.BlockIndex, spacer.InlineIndex, 0);
        DocumentPosition end = new(owner.BlockIndex, spacer.InlineIndex, spacer.TextLength);
        Assert.Equal(semanticNode.TextStart, semantics.TextOffsetFromDocumentPosition(start));
        Assert.Equal(semanticNode.TextEnd, semantics.TextOffsetFromDocumentPosition(end));
        Assert.Contains("first paragraph", semantics.Text, StringComparison.Ordinal);
        Assert.Contains("last paragraph", semantics.Text, StringComparison.Ordinal);

        DocumentPosition? roundTrip = semantics.PositionFromTextOffset(semanticNode.TextStart);
        Assert.NotNull(roundTrip);
        Assert.Equal(semanticNode.TextStart, semantics.TextOffsetFromDocumentPosition(roundTrip.Value));
    }

    [Fact]
    public void HtmlBlockImageSpacerUsesTheSameAdmissionPolicy()
    {
        const string source = "<div><img width='16' height='1' alt=''></div>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);

        InlineSpacerRun spacer = Assert.Single(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
        Assert.Equal(16f, spacer.Width);
        Assert.Equal(1f, spacer.Height);
        Assert.DoesNotContain(FlattenRuns(snapshot), static run => run is InlineImageRun);
    }

    [Theory]
    [InlineData("<img width='16' height='1'>")]
    [InlineData("<img alt=' ' width='16' height='1'>")]
    [InlineData("<img alt='' width='16'>")]
    [InlineData("<img alt='' height='1'>")]
    [InlineData("<img alt='' width='0' height='1'>")]
    [InlineData("<img alt='' width='16' height='0'>")]
    [InlineData("<img alt='' width='100%' height='1'>")]
    [InlineData("<img alt='' width='16' height='25%'>")]
    [InlineData("<img alt='' width='16' height='1' src=''>")]
    [InlineData("<img alt='' width='16' height='1' src='javascript:alert(1)'>")]
    [InlineData("<img alt='' width='16' height='1' srcset='one.png 1x'>")]
    [InlineData("<img alt='' width='16' height='1' data-src='one.png'>")]
    [InlineData("<img alt='' width='16' height='1' data-srcset='one.png 1x'>")]
    [InlineData("<img alt='' width='16' height='1' data-lazy-srcset='one.png 1x'>")]
    [InlineData("<img alt='' width='16' height='1' data-original='one.png'>")]
    [InlineData("<img alt='' width='16' height='1' data-original-src='one.png'>")]
    [InlineData("<img alt='' width='16' height='1' data-original-srcset='one.png 1x'>")]
    [InlineData("<img alt='' width='16' height='1' data-lazy-src='one.png'>")]
    [InlineData("<img alt='' width='NaN' height='1'>")]
    [InlineData("<img alt='' width='Infinity' height='1'>")]
    [InlineData("<img alt='' width='16' height='Infinity'>")]
    [InlineData("<img alt='' width='16' height='-Infinity'>")]
    [InlineData("<img alt='' style='width:NaNpx;height:1px'>")]
    [InlineData("<picture><img alt='' width='16' height='1'></picture>")]
    [InlineData("<a href='https://example.test/'><img alt='' width='16' height='1'></a>")]
    public void InertSpacerAdmissionRejectsIncompleteSourcedOrInteractiveImages(string source)
    {
        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);

        Assert.Empty(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
    }

    [Fact]
    public void InertSpacerAdmissionRespectsDisabledImageFeature()
    {
        const string source = "<img alt='' width='16' height='1'>";
        var options = new SafeHtmlOptions(enableImages: false);

        using LayoutSnapshot snapshot = Build(source, options);

        Assert.Empty(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
    }

    [Theory]
    [InlineData("<p><a href='https://example.test/'><img alt='' width='16' height='1'></a></p>")]
    [InlineData("<p><a href='javascript:alert(1)'><img alt='' width='16' height='1'></a></p>")]
    [InlineData("<div><a href='https://example.test/'><img alt='' width='16' height='1'></a></div>")]
    public void InertSpacerAdmissionRejectsAnchorContentWhenLinksAreDisabled(string source)
    {
        var options = new SafeHtmlOptions(enableLinks: false, enableImages: true);

        using LayoutSnapshot snapshot = Build(source, options);

        Assert.Empty(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
        Assert.DoesNotContain(FlattenRuns(snapshot), static run => run is LinkRun);
    }

    [Fact]
    public void InertSpacerDimensionsUseTheExistingAbsoluteLengthSafetyCap()
    {
        const string source = "<img alt='' width='9000' height='1'>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);

        InlineSpacerRun spacer = Assert.Single(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
        Assert.Equal(SafeHtmlParser.MaximumAbsoluteLength, spacer.Width);
        Assert.Equal(1f, spacer.Height);
    }

    [Fact]
    public void LargeDecorativeSpacerShrinksProportionallyWithoutPinningTableMinContent()
    {
        const string source = """
            | Preview | Description |
            | --- | --- |
            | <img alt='' width='8192' height='8192'> | A compact label |
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source, availableWidth: 180);
        TableBox table = Assert.Single(FlattenBoxes(snapshot).OfType<TableBox>());
        InlineSpacerRun spacer = Assert.Single(
            table.GetCellBoxes().SelectMany(static box => box.Runs).OfType<InlineSpacerRun>());
        InlineContainerBox owner = Assert.Single(
            table.GetCellBoxes(),
            box => box.Runs.Contains(spacer));
        Rect spacerBounds = owner.GetRunRect(spacer.InlineIndex);

        // Authored geometry remains immutable; only the layout projection
        // follows max-width:100% in the narrow table cell.
        Assert.Equal(8192f, spacer.Width);
        Assert.Equal(8192f, spacer.Height);
        Assert.False(table.CanScrollHorizontally);
        Assert.True(table.HorizontalExtent <= table.HorizontalViewport + 0.5);
        Assert.InRange(spacerBounds.Width, 1, owner.Bounds.Width + 0.5);
        Assert.InRange(table.Bounds.Height, 1, 256);
    }
}
