using System.Collections;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

[Collection("Shared Canvas text format cache")]
public sealed class InlineCodePaddingLayoutTests
{
    private const float LayoutWidth = 640f;
    private static readonly Color CodeSurface = Color.FromArgb(255, 230, 20, 110);
    private static readonly Color TextColor = Color.FromArgb(255, 20, 30, 40);
    private static readonly Color SelectionColor = Color.FromArgb(255, 180, 190, 200);
    private static readonly FieldInfo LayoutField = typeof(InlineContainerBox).GetField(
        "_layout",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo BufferField = typeof(InlineContainerBox).GetField(
        "_buffer",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo EnsureSelectionLayoutMethod = typeof(InlineContainerBox).GetMethod(
        "EnsureSelectionLayout",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ApplyInlineCodePaddingRangesMethod = typeof(InlineContainerBox).GetMethod(
        "ApplyInlineCodePaddingRanges",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly Type InlineCodePaddingRangeType = typeof(InlineContainerBox).GetNestedType(
        "InlineCodePaddingRange",
        BindingFlags.NonPublic)!;
    private static readonly MethodInfo FindLastTextElementStartMethod = typeof(InlineContainerBox).GetMethod(
        "FindLastTextElementStart",
        BindingFlags.Static | BindingFlags.NonPublic,
        binder: null,
        types: [typeof(string), typeof(int), typeof(int), typeof(CancellationToken)],
        modifiers: null)!;

    private readonly ITestOutputHelper _output;

    public InlineCodePaddingLayoutTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void PrimerLightV5ApiKeyIntrinsicMinimumFitsOnOneLineAtItsExactWidth()
    {
        const string code = "apiKey";
        GitHubReadmeVisualStyleProfile profile = GitHubReadmeVisualStyleProfiles.PrimerLightV5;
        ThemeSnapshot paddedTheme = CreateProfileTheme(profile.StyleSheet, profile.SurfaceColor);
        MarkdownStyleSheet noCodePaddingSheet = profile.StyleSheet.WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.InlineCode),
            new ElementStyleOverride { Padding = new Thickness(0) }));
        ThemeSnapshot noPaddingTheme = CreateProfileTheme(noCodePaddingSheet, profile.SurfaceColor);
        ElementStyle codeStyle = paddedTheme.GetStyle(MarkdownElementKeys.CodeInline);
        ElementStyle tableCellStyle = paddedTheme.GetStyle(MarkdownElementKeys.TableCell);

        Assert.Equal("Consolas", codeStyle.FontFamily);
        Assert.Equal(13.6f, codeStyle.FontSize, precision: 2);
        Assert.Equal(new Thickness(5.44, 2.72, 5.44, 2.72), codeStyle.Padding);
        Assert.Equal(new Thickness(13, 6, 13, 6), tableCellStyle.Padding);

        InlineContainerBox padded = CreateIntrinsicCodeBox(code, paddedTheme);
        using IDisposable paddedLease = Own(padded);
        InlineContainerBox withoutCodePadding = CreateIntrinsicCodeBox(code, noPaddingTheme);
        using IDisposable noPaddingLease = Own(withoutCodePadding);

        IntrinsicWidthMetrics paddedMetrics = padded.MeasureIntrinsicWidths();
        IntrinsicWidthMetrics noPaddingMetrics = withoutCodePadding.MeasureIntrinsicWidths();
        float expectedHorizontalCodePadding = (float)(codeStyle.Padding.Left + codeStyle.Padding.Right);
        float expectedCellMinimum = paddedMetrics.Minimum +
            (float)(tableCellStyle.Padding.Left + tableCellStyle.Padding.Right);
        float noPaddingCellMinimum = noPaddingMetrics.Minimum +
            (float)(tableCellStyle.Padding.Left + tableCellStyle.Padding.Right);

        Assert.Equal(expectedHorizontalCodePadding,
            paddedMetrics.Minimum - noPaddingMetrics.Minimum,
            precision: 2);
        Assert.Equal(expectedHorizontalCodePadding,
            paddedMetrics.Preferred - noPaddingMetrics.Preferred,
            precision: 2);
        Assert.Equal(expectedHorizontalCodePadding,
            expectedCellMinimum - noPaddingCellMinimum,
            precision: 2);
        float measuredHeight = padded.Measure(paddedMetrics.Minimum);
        Assert.Single(Layout(padded).LineMetrics);
        padded.Arrange(0, 0, paddedMetrics.Minimum);

        _output.WriteLine(
            $"profile={profile.Id}; text={code}; unpaddedMinimum={noPaddingMetrics.Minimum:R}; " +
            $"paddedMinimum={paddedMetrics.Minimum:R}; preferred={paddedMetrics.Preferred:R}; " +
            $"tableCellHorizontalInsets=26; paddedCellMinimum={expectedCellMinimum:R}; " +
            $"unpaddedCellMinimum={noPaddingCellMinimum:R}; exactMinimumLineCount={Layout(padded).LineMetrics.Length}; " +
            $"measuredHeightAtExactMinimum={measuredHeight:R}");
    }

    [Fact]
    public void InlineCodePaddingReservesOnlyOuterAdvancesAndKeepsSourceSelectionOffsets()
    {
        const string prefix = "before ";
        const string code = "aLongInlineIdentifier";
        const string suffix = " after";
        const string source = prefix + code + suffix;
        Thickness padding = new(5.44, 2.72, 5.44, 2.72);

        InlineContainerBox plain = CreateDirectBox(
            source,
            prefix,
            code,
            suffix,
            new Thickness(0),
            codeRun: false,
            FlowDirection.LeftToRight);
        using IDisposable plainLease = Own(plain);
        InlineContainerBox padded = CreateDirectBox(
            source,
            prefix,
            code,
            suffix,
            padding,
            codeRun: true,
            FlowDirection.LeftToRight);
        using IDisposable paddedLease = Own(padded);

        InlineRun codeRun = Assert.Single(padded.Runs, run => run is CodeInlineRun);
        int codeStart = prefix.Length;
        Assert.Equal(source, Buffer(padded));
        Assert.Equal(code, codeRun.Text);
        Assert.Equal(code.Length, codeRun.TextLength);
        Assert.Equal(new SourceSpan(codeStart, code.Length), codeRun.SourceSpan);

        CanvasTextLayout plainLayout = Layout(plain);
        CanvasTextLayout paddedLayout = Layout(padded);
        Rect plainCode = Union(plainLayout.GetCharacterRegions(codeStart, code.Length));
        Rect paddedCode = Union(paddedLayout.GetCharacterRegions(codeStart, code.Length));
        Assert.Equal(plainCode.Left, paddedCode.Left, precision: 3);
        Assert.Equal(padding.Left + padding.Right,
            (float)(paddedCode.Right - plainCode.Right), precision: 2);
        Rect plainSuffix = Union(plainLayout.GetCharacterRegions(codeStart + code.Length, suffix.Length));
        Rect paddedSuffix = Union(paddedLayout.GetCharacterRegions(codeStart + code.Length, suffix.Length));
        Assert.Equal(padding.Left + padding.Right,
            (float)(paddedSuffix.Left - plainSuffix.Left), precision: 2);
        Assert.Equal(source.Length, codeStart + codeRun.TextLength + suffix.Length);
        Assert.Equal(0f, paddedLayout.GetLeadingCharacterSpacing(codeStart - 1));
        Assert.Equal(0f, paddedLayout.GetTrailingCharacterSpacing(codeStart - 1));
        Assert.Equal(0f, paddedLayout.GetLeadingCharacterSpacing(codeStart + code.Length));
        Assert.Equal(0f, paddedLayout.GetTrailingCharacterSpacing(codeStart + code.Length));
        Assert.Equal(padding.Left, paddedLayout.GetLeadingCharacterSpacing(codeStart), precision: 2);
        Assert.Equal(padding.Right, paddedLayout.GetTrailingCharacterSpacing(codeStart + code.Length - 1), precision: 2);
        Assert.Equal(
            plainLayout.LineMetrics.Select(line => line.Height),
            paddedLayout.LineMetrics.Select(line => line.Height));

        // Spacing is native advance metadata only; the layout string and all
        // logical/document offsets remain the original UTF-16 sequence.
        var codeStartPosition = new DocumentPosition(padded.BlockIndex, codeRun.InlineIndex, 0);
        var codeEndPosition = new DocumentPosition(padded.BlockIndex, codeRun.InlineIndex, codeRun.TextLength);
        Assert.Equal(codeStart, padded.GetBufferCharOffset(codeStartPosition));
        Assert.Equal(codeStartPosition, padded.GetPositionFromBufferOffset(codeStart));
        Assert.Equal(codeStart + code.Length, padded.GetBufferCharOffset(codeEndPosition));
        InlineRun suffixRun = padded.Runs[codeRun.InlineIndex + 1];
        Assert.Equal(
            new DocumentPosition(padded.BlockIndex, suffixRun.InlineIndex, 0),
            padded.GetPositionFromBufferOffset(codeStart + code.Length));

        var selectionRange = new DocumentRange(codeStartPosition, codeEndPosition);
        Rect selectedCode = UnionRects(padded.GetSelectionRects(selectionRange));
        Assert.Equal(paddedCode.Left, selectedCode.Left, precision: 3);
        Assert.Equal(paddedCode.Right, selectedCode.Right, precision: 3);
        CanvasTextLayout selectionLayout = Assert.IsType<CanvasTextLayout>(
            EnsureSelectionLayoutMethod.Invoke(
                padded,
                [SelectionColor, CreateTheme(padding).GetStyle(MarkdownElementKeys.Body)]));
        Rect selectedLayoutCode = Union(selectionLayout.GetCharacterRegions(codeStart, code.Length));
        Assert.Equal(paddedCode.Left, selectedLayoutCode.Left, precision: 3);
        Assert.Equal(paddedCode.Right, selectedLayoutCode.Right, precision: 3);

        IntrinsicWidthMetrics plainIntrinsic = plain.MeasureIntrinsicWidths();
        IntrinsicWidthMetrics paddedIntrinsic = padded.MeasureIntrinsicWidths();
        Assert.Equal(padding.Left + padding.Right, paddedIntrinsic.Preferred - plainIntrinsic.Preferred, precision: 1);
        Assert.Equal(padding.Left + padding.Right, paddedIntrinsic.Minimum - plainIntrinsic.Minimum, precision: 1);
    }

    [Fact]
    public void NestedLinkedCodeUsesCodePaddingWithoutChangingLinkTextOrOffsets()
    {
        const string text = "before code after";
        const string code = "code";
        const int codeStart = 7;
        Thickness padding = new(7, 1.5, 3, 2.5);
        var builder = new LinkInlineStyleMapBuilder();
        LinkInlineStylePath codePath = builder.CreatePath(
            MarkdownElementKeys.CodeInline,
            Array.Empty<string>(),
            parent: null);
        builder.AddRange(codeStart, code.Length, codePath);
        LinkInlineStyleMap styleMap = Assert.IsType<LinkInlineStyleMap>(builder.Build());

        InlineContainerBox plain = CreateLinkBox(text, sourceMap: null, new Thickness(0));
        using IDisposable plainLease = Own(plain);
        InlineContainerBox styled = CreateLinkBox(text, styleMap, padding);
        using IDisposable styledLease = Own(styled);
        LinkRun link = Assert.IsType<LinkRun>(Assert.Single(styled.Runs));

        Assert.Equal(text, link.Text);
        Assert.Equal(text.Length, link.TextLength);
        Assert.Equal(text, Buffer(styled));
        Assert.Equal(codeStart, styled.GetBufferCharOffset(new DocumentPosition(styled.BlockIndex, link.InlineIndex, codeStart)));
        Assert.Equal(
            new DocumentPosition(styled.BlockIndex, link.InlineIndex, codeStart + code.Length),
            styled.GetPositionFromBufferOffset(codeStart + code.Length));

        CanvasTextLayout plainLayout = Layout(plain);
        CanvasTextLayout styledLayout = Layout(styled);
        Rect plainCode = Union(plainLayout.GetCharacterRegions(codeStart, code.Length));
        Rect styledCode = Union(styledLayout.GetCharacterRegions(codeStart, code.Length));
        Assert.Equal(plainCode.Left, styledCode.Left, precision: 3);
        Assert.Equal(padding.Left + padding.Right,
            (float)(styledCode.Right - plainCode.Right), precision: 2);
        Rect plainAfter = Union(plainLayout.GetCharacterRegions(
            codeStart + code.Length, text.Length - codeStart - code.Length));
        Rect styledAfter = Union(styledLayout.GetCharacterRegions(
            codeStart + code.Length, text.Length - codeStart - code.Length));
        Assert.Equal(padding.Left + padding.Right,
            (float)(styledAfter.Left - plainAfter.Left), precision: 2);
        Assert.Equal(padding.Left, Layout(styled).GetLeadingCharacterSpacing(codeStart), precision: 2);
        Assert.Equal(padding.Right, Layout(styled).GetTrailingCharacterSpacing(codeStart + code.Length - 1), precision: 2);
    }

    [Fact]
    public void LinkNestedInsideInlineCodePreservesCodePadding()
    {
        const string code = "linkedCode";
        Thickness padding = new(7, 1.5, 3, 2.5);
        var builder = new LinkInlineStyleMapBuilder();
        LinkInlineStylePath codePath = builder.CreatePath(
            MarkdownElementKeys.CodeInline,
            Array.Empty<string>(),
            parent: null);
        LinkInlineStylePath nestedLinkPath = builder.CreatePath(
            MarkdownElementKeys.Link,
            Array.Empty<string>(),
            parent: codePath);
        builder.AddRange(0, code.Length, nestedLinkPath);
        LinkInlineStyleMap styleMap = Assert.IsType<LinkInlineStyleMap>(builder.Build());

        InlineContainerBox plain = CreateLinkBox(code, sourceMap: null, padding);
        using IDisposable plainLease = Own(plain);
        InlineContainerBox box = CreateLinkBox(code, styleMap, padding);
        using IDisposable boxLease = Own(box);
        CanvasTextLayout layout = Layout(box);

        Assert.Equal(padding.Left, layout.GetLeadingCharacterSpacing(0), precision: 2);
        Assert.Equal(padding.Right, layout.GetTrailingCharacterSpacing(code.Length - 1), precision: 2);
        Assert.Equal(Layout(plain).LineMetrics.Select(line => line.Height),
            layout.LineMetrics.Select(line => line.Height));
        Assert.Equal(code, Buffer(box));
    }

    [Fact]
    public void AdjacentDistinctLinkedCodeElementsKeepSeparatePaddedChipBoundaries()
    {
        Thickness padding = new(7, 0, 3, 0);
        var builder = new LinkInlineStyleMapBuilder();
        LinkInlineStylePath firstCodePath = builder.CreatePath(
            MarkdownElementKeys.CodeInline,
            Array.Empty<string>(),
            parent: null);
        LinkInlineStylePath secondCodePath = builder.CreatePath(
            MarkdownElementKeys.CodeInline,
            Array.Empty<string>(),
            parent: null);
        builder.AddRange(0, 1, firstCodePath);
        builder.AddRange(1, 1, secondCodePath);
        LinkInlineStyleMap styleMap = Assert.IsType<LinkInlineStyleMap>(builder.Build());

        InlineContainerBox box = CreateLinkBox("ab", styleMap, padding);
        using IDisposable boxLease = Own(box);
        CanvasTextLayout layout = Layout(box);

        Assert.Equal(padding.Left, layout.GetLeadingCharacterSpacing(0), precision: 2);
        Assert.Equal(padding.Right, layout.GetTrailingCharacterSpacing(0), precision: 2);
        Assert.Equal(padding.Left, layout.GetLeadingCharacterSpacing(1), precision: 2);
        Assert.Equal(padding.Right, layout.GetTrailingCharacterSpacing(1), precision: 2);
    }

    [Fact]
    public void WrappedInlineCodePadsOnlyItsLogicalOuterEndpointsAndVerticalPaddingIsPaintOnly()
    {
        const string prefix = "prefix ";
        const string code = "alpha beta gamma delta epsilon zeta";
        const string suffix = " tail";
        const string source = prefix + code + suffix;
        Thickness withoutVerticalPadding = new(6, 0, 4, 0);
        Thickness withVerticalPadding = new(6, 8, 4, 11);

        InlineContainerBox noVertical = CreateDirectBox(
            source, prefix, code, suffix, withoutVerticalPadding, codeRun: true,
            FlowDirection.LeftToRight, width: 105f);
        using IDisposable noVerticalLease = Own(noVertical);
        InlineContainerBox vertical = CreateDirectBox(
            source, prefix, code, suffix, withVerticalPadding, codeRun: true,
            FlowDirection.LeftToRight, width: 105f);
        using IDisposable verticalLease = Own(vertical);

        CanvasTextLayout layout = Layout(vertical);
        int start = prefix.Length;
        int end = start + code.Length;
        CanvasTextLayoutRegion[] regions = Assert.IsType<CanvasTextLayoutRegion[]>(
            layout.GetCharacterRegions(start, code.Length));
        Assert.True(regions.Length > 1, $"Expected wrapped code fragments; got {regions.Length}.");
        Assert.Equal(withVerticalPadding.Left, layout.GetLeadingCharacterSpacing(start), precision: 2);
        Assert.Equal(withVerticalPadding.Right, layout.GetTrailingCharacterSpacing(end - 1), precision: 2);
        Assert.Equal(0f, layout.GetLeadingCharacterSpacing(start + 1));
        Assert.Equal(0f, layout.GetTrailingCharacterSpacing(end - 2));
        Assert.Equal(Buffer(noVertical), Buffer(vertical));
        Assert.Equal(Layout(noVertical).LineMetrics.Select(line => line.Height), layout.LineMetrics.Select(line => line.Height));
        Assert.Equal(Layout(noVertical).LayoutBounds.Height, layout.LayoutBounds.Height, precision: 3);

        // The style run remains one ordinary text range despite multiple visual
        // fragments. There are no inserted spaces, placeholders, or new indices.
        CodeInlineRun codeRun = Assert.IsType<CodeInlineRun>(vertical.Runs[1]);
        Assert.Equal(code.Length, codeRun.TextLength);
        Assert.Equal(code, codeRun.Text);
        Assert.Equal(source.Length, start + codeRun.TextLength + suffix.Length);
    }

    [Fact]
    public void AsymmetricPhysicalPaddingMapsToVisualEdgesForRightToLeftCode()
    {
        const string text = "אבגדה";
        Thickness padding = new(3, 0, 9, 0);
        InlineContainerBox plain = CreateDirectBox(
            text, string.Empty, text, string.Empty, new Thickness(0), codeRun: false,
            FlowDirection.RightToLeft);
        using IDisposable plainLease = Own(plain);
        InlineContainerBox padded = CreateDirectBox(
            text, string.Empty, text, string.Empty, padding, codeRun: true,
            FlowDirection.RightToLeft);
        using IDisposable paddedLease = Own(padded);

        CanvasTextLayout plainLayout = Layout(plain);
        CanvasTextLayout paddedLayout = Layout(padded);
        Vector2 plainLeading = plainLayout.GetCaretPosition(0, trailingSideOfCharacter: false);
        Vector2 plainTrailing = plainLayout.GetCaretPosition(0, trailingSideOfCharacter: true);
        Assert.True(plainLeading.X > plainTrailing.X, "The Hebrew leading edge should be on the physical right.");

        Rect plainBounds = Union(plainLayout.GetCharacterRegions(0, text.Length));
        Rect paddedBounds = Union(paddedLayout.GetCharacterRegions(0, text.Length));
        Assert.Equal(padding.Left + padding.Right,
            (float)(plainBounds.Left - paddedBounds.Left), precision: 2);
        Assert.Equal(plainBounds.Right, paddedBounds.Right, precision: 3);
        Assert.Equal(padding.Right, paddedLayout.GetLeadingCharacterSpacing(0), precision: 2);
        Assert.Equal(padding.Left, paddedLayout.GetTrailingCharacterSpacing(text.Length - 1), precision: 2);
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight, "abcאבג")]
    [InlineData(FlowDirection.LeftToRight, "אבגabc")]
    [InlineData(FlowDirection.LeftToRight, "(abc, אבג!)")]
    [InlineData(FlowDirection.LeftToRight, "(אבג, abc!)")]
    [InlineData(FlowDirection.RightToLeft, "abcאבג")]
    [InlineData(FlowDirection.RightToLeft, "אבגabc")]
    [InlineData(FlowDirection.RightToLeft, "(abc, אבג!)")]
    [InlineData(FlowDirection.RightToLeft, "(אבג, abc!)")]
    public void MixedBidiCodePaddingRetainsBothAdvancesAndUsesMeasuredEndpointCarets(
        FlowDirection flowDirection,
        string code)
    {
        Thickness padding = new(3, 0, 9, 0);
        InlineContainerBox plain = CreateDirectBox(
            code, string.Empty, code, string.Empty, new Thickness(0), codeRun: false,
            flowDirection);
        using IDisposable plainLease = Own(plain);
        InlineContainerBox padded = CreateDirectBox(
            code, string.Empty, code, string.Empty, padding, codeRun: true,
            flowDirection);
        using IDisposable paddedLease = Own(padded);

        CanvasTextLayout plainLayout = Layout(plain);
        CanvasTextLayout paddedLayout = Layout(padded);
        Rect plainBounds = Union(plainLayout.GetCharacterRegions(0, code.Length));
        Rect paddedBounds = Union(paddedLayout.GetCharacterRegions(0, code.Length));
        float leftExpansion = (float)(plainBounds.Left - paddedBounds.Left);
        float rightExpansion = (float)(paddedBounds.Right - plainBounds.Right);
        string trace = $"plain={DescribeTextLayout(plainLayout, code)} padded={DescribeTextLayout(paddedLayout, code)}";
        float expectedLeftExpansion = flowDirection == FlowDirection.RightToLeft
            ? (float)(padding.Left + padding.Right)
            : 0f;
        float expectedRightExpansion = flowDirection == FlowDirection.RightToLeft
            ? 0f
            : (float)(padding.Left + padding.Right);
        Assert.True(System.Math.Abs(leftExpansion - expectedLeftExpansion) < 0.01f,
            $"Expected paragraph-anchored left expansion {expectedLeftExpansion}, got {leftExpansion}. {trace}");
        Assert.True(System.Math.Abs(rightExpansion - expectedRightExpansion) < 0.01f,
            $"Expected paragraph-anchored right expansion {expectedRightExpansion}, got {rightExpansion}. {trace}");
        Assert.Equal(code, Buffer(padded));
    }

    [Fact]
    public void InlineCodePaddingRangeChecksCancellationBeforeCaretAndGraphemeWork()
    {
        const string code = "inline code endpoint";
        Thickness padding = new(3, 0, 9, 0);
        InlineContainerBox box = CreateDirectBox(
            code, string.Empty, code, string.Empty, padding, codeRun: true,
            FlowDirection.LeftToRight);
        using IDisposable boxLease = Own(box);
        using var cancellation = new CancellationTokenSource();
        box.Context.SetCancellationToken(cancellation.Token);
        cancellation.Cancel();

        IList ranges = CreatePaddingRanges(0, code.Length, (float)padding.Left, (float)padding.Right);
        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            ApplyInlineCodePaddingRangesMethod.Invoke(
                box, [Layout(box), ranges, true]));
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public void InlineCodeGraphemeScanChecksCancellationAtEntry()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            FindLastTextElementStartMethod.Invoke(
                null, ["a\u0301", 0, 2, cancellation.Token]));
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public void SelectionLayoutStillCompletesAfterTheOwningLayoutTokenIsCancelled()
    {
        const string code = "selection code";
        InlineContainerBox box = CreateDirectBox(
            code, string.Empty, code, string.Empty, new Thickness(3, 0, 9, 0),
            codeRun: true, FlowDirection.LeftToRight);
        using IDisposable boxLease = Own(box);
        using var cancellation = new CancellationTokenSource();
        box.Context.SetCancellationToken(cancellation.Token);
        cancellation.Cancel();

        CanvasTextLayout selectionLayout = Assert.IsType<CanvasTextLayout>(
            EnsureSelectionLayoutMethod.Invoke(
                box,
                [SelectionColor, CreateTheme(new Thickness(3, 0, 9, 0))
                    .GetStyle(MarkdownElementKeys.Body)]));
        Assert.NotNull(selectionLayout);
    }

    [Fact]
    public void GraphemeAndSurrogateEndpointsKeepOriginalUtf16RangeAndAcceptPadding()
    {
        const string prefix = "x";
        const string code = "😀e\u0301";
        const string suffix = "y";
        const string source = prefix + code + suffix;
        Thickness padding = new(4, 2, 6, 3);
        InlineContainerBox plain = CreateDirectBox(
            source, prefix, code, suffix, new Thickness(0), codeRun: false,
            FlowDirection.LeftToRight);
        using IDisposable plainLease = Own(plain);
        InlineContainerBox padded = CreateDirectBox(
            source, prefix, code, suffix, padding, codeRun: true,
            FlowDirection.LeftToRight);
        using IDisposable paddedLease = Own(padded);

        int start = prefix.Length;
        InlineRun run = Assert.Single(padded.Runs, item => item is CodeInlineRun);
        Assert.Equal(4, run.TextLength); // emoji surrogate pair + base + combining mark
        Assert.Equal(source, Buffer(padded));
        Rect baseline = Union(Layout(plain).GetCharacterRegions(start, run.TextLength));
        Rect actual = Union(Layout(padded).GetCharacterRegions(start, run.TextLength));
        Assert.Equal(baseline.Left, actual.Left, precision: 3);
        Rect plainSuffix = Union(Layout(plain).GetCharacterRegions(
            start + run.TextLength, suffix.Length));
        Rect paddedSuffix = Union(Layout(padded).GetCharacterRegions(
            start + run.TextLength, suffix.Length));
        Assert.Equal(padding.Left + padding.Right,
            (float)(paddedSuffix.Left - plainSuffix.Left), precision: 2);
        Assert.Equal(padding.Left, Layout(padded).GetLeadingCharacterSpacing(start), precision: 2);
        Assert.Equal(padding.Left, Layout(padded).GetLeadingCharacterSpacing(start + 1), precision: 2);
        Assert.Equal(padding.Right, Layout(padded).GetTrailingCharacterSpacing(start + 2), precision: 2);
        Assert.Equal(padding.Right, Layout(padded).GetTrailingCharacterSpacing(start + 3), precision: 2);
        Assert.Equal(start + run.TextLength, padded.GetBufferCharOffset(
            new DocumentPosition(padded.BlockIndex, run.InlineIndex, run.TextLength)));
        InlineRun suffixRun = padded.Runs[run.InlineIndex + 1];
        Assert.Equal(new DocumentPosition(padded.BlockIndex, suffixRun.InlineIndex, 0),
            padded.GetPositionFromBufferOffset(start + run.TextLength));
    }

    [Fact]
    public void TrailingPaddingAnchorsBeforeFinalSurrogatePair()
    {
        const string prefix = "before ";
        const string code = "token😀";
        const string suffix = " after";
        const string source = prefix + code + suffix;
        Thickness padding = new(2, 0, 8, 0);
        InlineContainerBox plain = CreateDirectBox(
            source, prefix, code, suffix, new Thickness(0), codeRun: false,
            FlowDirection.LeftToRight);
        using IDisposable plainLease = Own(plain);
        InlineContainerBox padded = CreateDirectBox(
            source, prefix, code, suffix, padding, codeRun: true,
            FlowDirection.LeftToRight);
        using IDisposable paddedLease = Own(padded);

        int start = prefix.Length;
        int highSurrogate = start + code.Length - 2;
        int lowSurrogate = highSurrogate + 1;
        CanvasTextLayout plainLayout = Layout(plain);
        CanvasTextLayout paddedLayout = Layout(padded);
        Rect plainSuffix = Union(plainLayout.GetCharacterRegions(start + code.Length, suffix.Length));
        Rect paddedSuffix = Union(paddedLayout.GetCharacterRegions(start + code.Length, suffix.Length));

        Assert.Equal(padding.Right, paddedLayout.GetTrailingCharacterSpacing(highSurrogate), precision: 2);
        Assert.Equal(padding.Right, paddedLayout.GetTrailingCharacterSpacing(lowSurrogate), precision: 2);
        Assert.Equal(padding.Left + padding.Right,
            (float)(paddedSuffix.Left - plainSuffix.Left), precision: 2);
        Assert.Equal(source, Buffer(padded));
    }

    [Fact]
    public void TrailingPaddingAnchorsBeforeFinalZwjEmojiTextElement()
    {
        const string code = "id👩‍💻";
        Thickness padding = new(3, 0, 9, 0);
        InlineContainerBox plain = CreateDirectBox(
            code, string.Empty, code, string.Empty, new Thickness(0), codeRun: false,
            FlowDirection.LeftToRight);
        using IDisposable plainLease = Own(plain);
        InlineContainerBox padded = CreateDirectBox(
            code, string.Empty, code, string.Empty, padding, codeRun: true,
            FlowDirection.LeftToRight);
        using IDisposable paddedLease = Own(padded);

        int lastTextElementStart = LastTextElementStart(code);
        int finalJoinedComponentStart = code.LastIndexOf('\u200D') + 1;
        Assert.Equal(2, lastTextElementStart);
        Assert.Equal(5, finalJoinedComponentStart);
        CanvasTextLayout plainLayout = Layout(plain);
        CanvasTextLayout paddedLayout = Layout(padded);
        Rect plainBounds = Union(plainLayout.GetCharacterRegions(0, code.Length));
        Rect paddedBounds = Union(paddedLayout.GetCharacterRegions(0, code.Length));

        float widthExpansion = (float)(paddedBounds.Width - plainBounds.Width);
        Assert.True(System.Math.Abs(widthExpansion - (padding.Left + padding.Right)) < 0.01f,
            $"Expected both padding advances, got {widthExpansion}. " +
            $"plain={DescribeTextLayout(plainLayout, code)} padded={DescribeTextLayout(paddedLayout, code)}");
        Assert.Equal(padding.Left, paddedLayout.GetLeadingCharacterSpacing(0), precision: 2);
        for (int index = lastTextElementStart; index < code.Length; index++)
        {
            Assert.Equal(padding.Right, paddedLayout.GetTrailingCharacterSpacing(index), precision: 2);
        }
        Assert.Equal(ClusterCharacterCounts(plainLayout), ClusterCharacterCounts(paddedLayout));
        Rect plainInkBounds = ReadCanvasTextLayoutRect(plainLayout, "DrawBounds");
        Rect paddedInkBounds = ReadCanvasTextLayoutRect(paddedLayout, "DrawBounds");
        Assert.Equal(plainInkBounds.Width, paddedInkBounds.Width, precision: 3);
        Assert.Equal(plainInkBounds.Height, paddedInkBounds.Height, precision: 3);
        Assert.Equal(plainInkBounds.Y, paddedInkBounds.Y, precision: 3);
        Assert.Equal(padding.Left, (float)(paddedInkBounds.X - plainInkBounds.X), precision: 2);
        Assert.Equal(code, Buffer(padded));
        Assert.Equal(code.Length, Assert.Single(padded.Runs, run => run is CodeInlineRun).TextLength);
    }

    [Fact]
    public void InlineCodePaddingUsesWholeTextElementsWithoutChangingShapingOrInk()
    {
        (string Code, FlowDirection Direction, int FirstStart, int FirstCount,
            int LastStart, int LastCount)[] cases =
        [
            ("id👩‍💻", FlowDirection.LeftToRight, 0, 1, 2, 5),
            ("👩‍💻id", FlowDirection.LeftToRight, 0, 5, 6, 1),
            ("👩‍💻", FlowDirection.LeftToRight, 0, 5, 0, 5),
            ("e\u0301id", FlowDirection.LeftToRight, 0, 2, 3, 1),
            ("ide\u0301", FlowDirection.LeftToRight, 0, 1, 2, 2),
            ("لا", FlowDirection.RightToLeft, 0, 1, 1, 1),
        ];

        foreach ((string code, FlowDirection direction, int firstStart, int firstCount,
            int lastStart, int lastCount) in cases)
        {
            InlineContainerBox baselineBox = CreateDirectBox(
                code, string.Empty, code, string.Empty, new Thickness(0), codeRun: false, direction);
            using IDisposable baselineLease = Own(baselineBox);
            InlineContainerBox paddedBox = CreateDirectBox(
                code, string.Empty, code, string.Empty, new Thickness(3, 0, 9, 0), codeRun: true, direction);
            using IDisposable paddedLease = Own(paddedBox);
            CanvasTextLayout baseline = Layout(baselineBox);
            CanvasTextLayout padded = Layout(paddedBox);
            Rect baselineBounds = Union(baseline.GetCharacterRegions(0, code.Length));
            Rect paddedBounds = Union(padded.GetCharacterRegions(0, code.Length));
            float expectedLeading = direction == FlowDirection.RightToLeft ? 9f : 3f;
            float expectedTrailing = direction == FlowDirection.RightToLeft ? 3f : 9f;
            Assert.Equal(12f, (float)(paddedBounds.Width - baselineBounds.Width), precision: 2);
            Assert.Equal(expectedLeading,
                padded.GetLeadingCharacterSpacing(firstStart), precision: 2);
            Assert.Equal(expectedTrailing,
                padded.GetTrailingCharacterSpacing(lastStart), precision: 2);
            for (int index = firstStart; index < firstStart + firstCount; index++)
            {
                Assert.Equal(expectedLeading,
                    padded.GetLeadingCharacterSpacing(index), precision: 2);
            }
            for (int index = lastStart; index < lastStart + lastCount; index++)
            {
                Assert.Equal(expectedTrailing,
                    padded.GetTrailingCharacterSpacing(index), precision: 2);
            }
            Assert.Equal(firstCount, TextElementLength(code, firstStart));
            Assert.Equal(lastCount, TextElementLength(code, lastStart));
            Assert.Equal(ClusterCharacterCounts(baseline), ClusterCharacterCounts(padded));
            Rect baselineInkBounds = ReadCanvasTextLayoutRect(baseline, "DrawBounds");
            Rect paddedInkBounds = ReadCanvasTextLayoutRect(padded, "DrawBounds");
            Assert.Equal(baselineInkBounds.Width, paddedInkBounds.Width, precision: 3);
            Assert.Equal(baselineInkBounds.Height, paddedInkBounds.Height, precision: 3);
            Assert.Equal(baselineInkBounds.Y, paddedInkBounds.Y, precision: 3);
            float expectedInkTranslation = direction == FlowDirection.RightToLeft ? -9f : 3f;
            Assert.Equal(expectedInkTranslation,
                (float)(paddedInkBounds.X - baselineInkBounds.X), precision: 2);
            Assert.Equal(code, Buffer(paddedBox));
        }
    }

    [Theory]
    [InlineData("abcאבג")]
    [InlineData("alpha אבג gamma")]
    public void MixedBidiPaddingRemainsAttachedWhenTheRangeWraps(string code)
    {
        const float leadingPadding = 3f;
        const float trailingPadding = 9f;
        const float wrapMargin = 0.25f;
        InlineContainerBox wide = CreateDirectBox(
            code, string.Empty, code, string.Empty, new Thickness(0), codeRun: false,
            FlowDirection.LeftToRight);
        using IDisposable wideLease = Own(wide);
        float naturalWidth = (float)Union(Layout(wide).GetCharacterRegions(0, code.Length)).Width;
        float constrainedWidth = naturalWidth + leadingPadding + trailingPadding - wrapMargin;

        InlineContainerBox plain = CreateDirectBox(
            code, string.Empty, code, string.Empty, new Thickness(0), codeRun: false,
            FlowDirection.LeftToRight, width: constrainedWidth);
        using IDisposable plainLease = Own(plain);
        InlineContainerBox padded = CreateDirectBox(
            code, string.Empty, code, string.Empty,
            new Thickness(leadingPadding, 0, trailingPadding, 0), codeRun: true,
            FlowDirection.LeftToRight, width: constrainedWidth);
        using IDisposable paddedLease = Own(padded);

        CanvasTextLayout plainLayout = Layout(plain);
        CanvasTextLayout paddedLayout = Layout(padded);
        var plainLines = plainLayout.LineMetrics;
        var paddedLines = paddedLayout.LineMetrics;
        Rect plainBounds = Union(plainLayout.GetCharacterRegions(0, code.Length));
        Rect paddedBounds = Union(paddedLayout.GetCharacterRegions(0, code.Length));
        string spacing = string.Join(",", Enumerable.Range(0, code.Length).Select(index =>
            $"{index}:{paddedLayout.GetLeadingCharacterSpacing(index):0.##}/" +
            $"{paddedLayout.GetTrailingCharacterSpacing(index):0.##}"));

        Assert.Single(plainLines);
        Assert.True(paddedLines.Length > plainLines.Length,
            $"Expected the added 12px to force a line break at {constrainedWidth:0.##}; " +
            $"plain={plainBounds}; padded={paddedBounds}; lines={paddedLines.Length}; spacing={spacing}; " +
            DescribeTextLayout(paddedLayout, code));
        Assert.Equal(12f,
            Enumerable.Range(0, code.Length).Sum(index =>
                paddedLayout.GetLeadingCharacterSpacing(index) + paddedLayout.GetTrailingCharacterSpacing(index)),
            precision: 2);
        Assert.Equal(code, Buffer(padded));
        _output.WriteLine(
            $"wrap width={constrainedWidth:0.###}; plainLines={plainLines.Length}; paddedLines={paddedLines.Length}; " +
            $"plainBounds={plainBounds}; paddedBounds={paddedBounds}; spacing={spacing}; " +
            $"plainLineHeights={string.Join(",", plainLines.Select(line => line.Height.ToString("0.###", CultureInfo.InvariantCulture)))}; " +
            $"paddedLineHeights={string.Join(",", paddedLines.Select(line => line.Height.ToString("0.###", CultureInfo.InvariantCulture)))}");
    }

    [Fact]
    public void ManyInlineCodeRangesPreserveTextAndApplyEachBoundary()
    {
        const int rangeCount = 128;
        const float leftPadding = 4f;
        const float rightPadding = 5f;
        Thickness padding = new(leftPadding, 0, rightPadding, 0);
        var source = new System.Text.StringBuilder(rangeCount * 2);
        var codeOffsets = new int[rangeCount];
        MarkdownLayoutContext context = CreateContext(
            new string('x', rangeCount * 2 - 1),
            CreateTheme(padding),
            FlowDirection.LeftToRight);
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
            UseContainerMargin = false,
            UseContainerPadding = false,
        };

        for (int index = 0; index < rangeCount; index++)
        {
            if (index != 0)
            {
                source.Append(' ');
                box.Add(new TextRun(" ")
                {
                    SourceSpan = new SourceSpan(source.Length - 1, 1),
                });
            }

            codeOffsets[index] = source.Length;
            source.Append('x');
            box.Add(new CodeInlineRun("x")
            {
                SourceSpan = new SourceSpan(source.Length - 1, 1),
            });
        }

        _ = box.Measure(10000f);
        box.Arrange(0, 0, 10000f);
        using (Own(box))
        {
            Assert.Equal(source.ToString(), Buffer(box));
            CanvasTextLayout layout = Layout(box);
            for (int index = 0; index < rangeCount; index++)
            {
                int offset = codeOffsets[index];
                Assert.Equal(leftPadding, layout.GetLeadingCharacterSpacing(offset));
                Assert.Equal(rightPadding, layout.GetTrailingCharacterSpacing(offset));
                Assert.Equal(new SourceSpan(offset, 1), box.Runs[index * 2].SourceSpan);
            }
        }
    }

    [Fact]
    public void ZeroHorizontalCodePaddingKeepsUnpaddedLayout()
    {
        const string code = "code without horizontal padding";
        Thickness verticalOnly = new(0, 3, 0, 4);
        InlineContainerBox plain = CreateDirectBox(
            code, string.Empty, code, string.Empty, new Thickness(0), codeRun: false,
            FlowDirection.LeftToRight);
        using IDisposable plainLease = Own(plain);
        InlineContainerBox padded = CreateDirectBox(
            code, string.Empty, code, string.Empty, verticalOnly, codeRun: true,
            FlowDirection.LeftToRight);
        using IDisposable paddedLease = Own(padded);

        CanvasTextLayout plainLayout = Layout(plain);
        CanvasTextLayout paddedLayout = Layout(padded);
        Assert.Equal(Buffer(plain), Buffer(padded));
        Assert.Equal(plainLayout.LayoutBounds.Width, paddedLayout.LayoutBounds.Width, precision: 3);
        Assert.Equal(plainLayout.LayoutBounds.Height, paddedLayout.LayoutBounds.Height, precision: 3);
        for (int index = 0; index < code.Length; index++)
        {
            Assert.Equal(0f, paddedLayout.GetLeadingCharacterSpacing(index));
            Assert.Equal(0f, paddedLayout.GetTrailingCharacterSpacing(index));
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void InlineCodeBackgroundUsesResolvedPaddingAtRasterScales(float scale)
    {
        const string code = "code";
        Thickness padding = new(5.44, 2.72, 5.44, 2.72);
        InlineContainerBox box = CreateDirectBox(
            code,
            string.Empty,
            code,
            string.Empty,
            padding,
            codeRun: true,
            FlowDirection.LeftToRight,
            width: 120f,
            rasterizationScale: scale,
            codeForeground: CodeSurface);
        using IDisposable boxLease = Own(box);
        CanvasTextLayout layout = Layout(box);
        Rect codeBounds = Union(layout.GetCharacterRegions(0, code.Length));
        double horizontalInteriorX = codeBounds.Left + 1.0;
        double paddedTop = codeBounds.Top + codeBounds.Height * 0.05 - padding.Top;
        int pixelWidth = (int)System.Math.Ceiling(120f * scale);
        int pixelHeight = (int)System.Math.Ceiling(System.Math.Max(60f, box.Bounds.Height + 16f) * scale);
        using var target = new CanvasRenderTarget(
            CanvasDevice.GetSharedDevice(),
            120f,
            System.Math.Max(60f, (float)box.Bounds.Height + 16f),
            96f * scale);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            box.Paint(drawing, new Rect(0, 0, 120, System.Math.Max(60f, box.Bounds.Height + 16f)));
        }

        Color[] pixels = target.GetPixelColors();
        int sampleX = System.Math.Clamp((int)System.Math.Floor(horizontalInteriorX * scale), 0, pixelWidth - 1);
        int sampleY = System.Math.Clamp((int)System.Math.Floor((paddedTop + 1.0) * scale), 0, pixelHeight - 1);
        Assert.Equal(CodeSurface, pixels[sampleY * pixelWidth + sampleX]);
    }

    private static InlineContainerBox CreateDirectBox(
        string source,
        string prefix,
        string code,
        string suffix,
        Thickness codePadding,
        bool codeRun,
        FlowDirection flowDirection,
        float width = LayoutWidth,
        double rasterizationScale = 1,
        Color? codeForeground = null)
    {
        MarkdownLayoutContext context = CreateContext(
            source,
            CreateTheme(codePadding, codeForeground),
            flowDirection,
            rasterizationScale);
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
            UseContainerMargin = false,
            UseContainerPadding = false,
        };

        if (prefix.Length > 0)
        {
            box.Add(new TextRun(prefix)
            {
                SourceSpan = new SourceSpan(0, prefix.Length),
            });
        }

        InlineRun inlineCode = codeRun ? new CodeInlineRun(code) : new TextRun(code);
        inlineCode.SourceSpan = new SourceSpan(prefix.Length, code.Length);
        box.Add(inlineCode);

        if (suffix.Length > 0)
        {
            box.Add(new TextRun(suffix)
            {
                SourceSpan = new SourceSpan(prefix.Length + code.Length, suffix.Length),
            });
        }

        _ = box.Measure(width);
        box.Arrange(0, 0, width);
        return box;
    }

    private static IDisposable Own(InlineContainerBox box)
        => new InlineContainerLease(box);

    private sealed class InlineContainerLease : IDisposable
    {
        private readonly InlineContainerBox _box;

        public InlineContainerLease(InlineContainerBox box)
        {
            _box = box;
        }

        public void Dispose() => _box.Dispose();
    }

    private static InlineContainerBox CreateLinkBox(
        string text,
        LinkInlineStyleMap? sourceMap,
        Thickness codePadding)
    {
        MarkdownLayoutContext context = CreateContext(
            text,
            CreateTheme(codePadding),
            FlowDirection.LeftToRight);
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
            UseContainerMargin = false,
            UseContainerPadding = false,
        };
        box.Add(new LinkRun(text, "https://example.test/code", null, sourceMap)
        {
            SourceSpan = new SourceSpan(0, text.Length),
        });
        _ = box.Measure(LayoutWidth);
        box.Arrange(0, 0, LayoutWidth);
        return box;
    }

    private static MarkdownLayoutContext CreateContext(
        string source,
        ThemeSnapshot theme,
        FlowDirection flowDirection,
        double rasterizationScale = 1)
        => new(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            new MarkdownExtensionRegistry(),
            flowDirection)
        {
            RasterizationScale = rasterizationScale,
        };

    private static InlineContainerBox CreateIntrinsicCodeBox(string code, ThemeSnapshot theme)
    {
        MarkdownLayoutContext context = CreateContext(code, theme, FlowDirection.LeftToRight);
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
            UseContainerMargin = false,
            UseContainerPadding = false,
        };
        box.Add(new CodeInlineRun(code)
        {
            SourceSpan = new SourceSpan(0, code.Length),
        });
        return box;
    }

    private static ThemeSnapshot CreateProfileTheme(MarkdownStyleSheet styleSheet, Color surfaceColor)
    {
        var body = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16f,
            Foreground = TextColor,
            LineHeightMultiplier = 1.5f,
        };
        return new ThemeSnapshot(
            new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = body,
            },
            new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
            surfaceColor,
            SelectionColor,
            TextColor,
            TextColor,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1,
            styleSheet: styleSheet);
    }

    private static ThemeSnapshot CreateTheme(Thickness codePadding, Color? codeForeground = null)
    {
        Color black = TextColor;
        var body = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16f,
            Foreground = black,
            LineHeightMultiplier = 1.4f,
        };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.Link] = new ElementStyle
            {
                FontFamily = body.FontFamily,
                FontSize = body.FontSize,
                Foreground = Color.FromArgb(255, 0, 80, 180),
                LineHeightMultiplier = body.LineHeightMultiplier,
            },
            [MarkdownElementKeys.CodeInline] = new ElementStyle
            {
                FontFamily = body.FontFamily,
                FontSize = body.FontSize,
                Foreground = codeForeground ?? black,
                Background = CodeSurface,
                CornerRadius = 0,
                Padding = codePadding,
                LineHeightMultiplier = body.LineHeightMultiplier,
            },
        };
        return new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
            Color.FromArgb(255, 255, 255, 255),
            Color.FromArgb(255, 180, 180, 180),
            Color.FromArgb(255, 0, 0, 0),
            Color.FromArgb(255, 0, 0, 0),
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
    }

    private static CanvasTextLayout Layout(InlineContainerBox box)
        => Assert.IsType<CanvasTextLayout>(LayoutField.GetValue(box));

    private static string Buffer(InlineContainerBox box)
        => Assert.IsType<string>(BufferField.GetValue(box));

    private static Rect Union(CanvasTextLayoutRegion[] regions)
    {
        Assert.NotNull(regions);
        Assert.NotEmpty(regions);
        double left = regions.Min(region => region.LayoutBounds.Left);
        double top = regions.Min(region => region.LayoutBounds.Top);
        double right = regions.Max(region => region.LayoutBounds.Right);
        double bottom = regions.Max(region => region.LayoutBounds.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static string DescribeTextLayout(CanvasTextLayout layout, string text)
    {
        var description = new System.Text.StringBuilder();
        CanvasTextLayoutRegion[] regions = layout.GetCharacterRegions(0, text.Length);
        description.Append("regions=");
        foreach (CanvasTextLayoutRegion region in regions)
            description.Append($"[{region.CharacterIndex}+{region.CharacterCount}@{region.LayoutBounds.Left:0.##},{region.LayoutBounds.Top:0.##},{region.LayoutBounds.Width:0.##}]");

        description.Append(" carets=");
        for (int index = 0; index < text.Length; index++)
        {
            Vector2 leading = layout.GetCaretPosition(index, trailingSideOfCharacter: false);
            Vector2 trailing = layout.GetCaretPosition(index, trailingSideOfCharacter: true);
            description.Append($"{index}:{(int)text[index]}({leading.X:0.##},{trailing.X:0.##})s({layout.GetLeadingCharacterSpacing(index):0.##},{layout.GetTrailingCharacterSpacing(index):0.##});");
        }

        return description.ToString();
    }

    private static int[] ClusterCharacterCounts(CanvasTextLayout layout)
    {
        object? metrics = typeof(CanvasTextLayout)
            .GetProperty("ClusterMetrics", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(layout);
        Assert.NotNull(metrics);
        Assert.IsAssignableFrom<System.Collections.IEnumerable>(metrics);
        return ((System.Collections.IEnumerable)metrics)
            .Cast<object>()
            .Select(metric => ReadIntegerMember(metric, "CharacterCount"))
            .ToArray();
    }

    private static int ReadIntegerMember(object instance, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = instance.GetType();
        object? value = type.GetProperty(name, flags)?.GetValue(instance) ??
            type.GetField(name, flags)?.GetValue(instance);
        Assert.NotNull(value);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static Rect ReadCanvasTextLayoutRect(CanvasTextLayout layout, string name)
    {
        object? value = typeof(CanvasTextLayout)
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(layout);
        return Assert.IsType<Rect>(value);
    }

    private static int TextElementLength(string text, int start)
    {
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text, start);
        Assert.True(elements.MoveNext());
        int firstElementStart = start + elements.ElementIndex;
        if (!elements.MoveNext())
            return text.Length - firstElementStart;

        return start + elements.ElementIndex - firstElementStart;
    }

    private static Rect UnionRects(IEnumerable<Rect> rects)
    {
        Rect[] values = rects.ToArray();
        Assert.NotEmpty(values);
        double left = values.Min(rect => rect.Left);
        double top = values.Min(rect => rect.Top);
        double right = values.Max(rect => rect.Right);
        double bottom = values.Max(rect => rect.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static IList CreatePaddingRanges(int start, int length, float leftPadding, float rightPadding)
    {
        Type listType = typeof(List<>).MakeGenericType(InlineCodePaddingRangeType);
        var ranges = (IList)Activator.CreateInstance(listType)!;
        ConstructorInfo constructor = InlineCodePaddingRangeType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(int), typeof(int), typeof(float), typeof(float)],
            modifiers: null)!;
        ranges.Add(constructor.Invoke([start, length, leftPadding, rightPadding]));
        return ranges;
    }

    private static int LastTextElementStart(string text)
    {
        int lastStart = 0;
        TextElementEnumerator elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
            lastStart = elements.ElementIndex;
        return lastStart;
    }
}
