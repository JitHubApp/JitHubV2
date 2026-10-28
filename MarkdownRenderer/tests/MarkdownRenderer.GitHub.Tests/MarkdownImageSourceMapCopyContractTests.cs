using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Markdig;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownImageSourceMapCopyContractTests
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task PromotedImageAfterTextKeepsAtomicEndInDocumentAndSourceCopy()
    {
        const string alt = "FinalSvgAtomicAltUnique";
        const string source = "FinalSvgBoundaryUnique\n\n![FinalSvgAtomicAltUnique](missing.svg)";
        using LayoutSnapshot snapshot = await Build(source);
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        ImageBox image = Assert.Single(snapshot.Blocks.OfType<ImageBox>());
        MarkdownTextSpan imageSpan = Assert.Single(
            semantic.TextSpans,
            span => ReferenceEquals(span.ImageBox, image));

        Assert.EndsWith(alt, semantic.Text);
        Assert.True(semantic.TryGetDocumentRange(0, semantic.Text.Length, out DocumentRange fullRange));

        // A promoted ImageBox has a single atomic source-map position. Its
        // after endpoint must map to the end of the accessible alt text, not
        // fall back to the first span at that block index.
        var afterImage = new DocumentPosition(image.BlockIndex, 0, 1);
        Assert.Equal(imageSpan.TextEnd, semantic.TextOffsetFromDocumentPosition(afterImage));
        Assert.Equal(afterImage, semantic.PositionFromTextOffset(imageSpan.TextEnd));
        Assert.Equal(semantic.Text.Length,
            semantic.TextOffsetFromDocumentPosition(fullRange.End));

        string imageMarkdown = $"![{alt}](missing.svg)";
        var imageRange = new DocumentRange(new DocumentPosition(image.BlockIndex, 0, 0), afterImage);
        Assert.Equal(imageMarkdown, snapshot.SourceMap.Slice(imageRange));
        Assert.Equal(source, snapshot.SourceMap.Slice(fullRange));

        object payload = InvokeRenderedSelectionPayload(snapshot, fullRange);
        Assert.Equal(semantic.Text, ReadPayloadField(payload, "Text"));
    }

    [Fact]
    public async Task PrecedingTextOnlyRangeDoesNotIncludeImageAtItsExclusiveEnd()
    {
        const string source = "VisibleBeforeImageUnique\n\n![ImageAfterExclusiveEndUnique](missing.svg)";
        using LayoutSnapshot snapshot = await Build(source);
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        ImageBox image = Assert.Single(snapshot.Blocks.OfType<ImageBox>());
        MarkdownTextSpan imageSpan = Assert.Single(
            semantic.TextSpans,
            span => ReferenceEquals(span.ImageBox, image));

        Rect[] precedingTextRects = semantic
            .GetDocumentRects(0, imageSpan.TextStart, expandDegenerate: false)
            .ToArray();
        Assert.NotEmpty(precedingTextRects);
        Assert.DoesNotContain(image.Bounds, precedingTextRects);

        // The same object remains visible to a caret at the boundary and to a
        // nondegenerate range that actually includes its semantic alt text.
        Assert.Contains(image.Bounds, semantic
            .GetDocumentRects(imageSpan.TextStart, imageSpan.TextStart, expandDegenerate: false));
        Assert.Contains(image.Bounds, semantic
            .GetDocumentRects(imageSpan.TextStart, imageSpan.TextEnd, expandDegenerate: false));
    }

    [Fact]
    public void BlockEmbedAndHostedElementAfterEndpointsMapToTheirSemanticEnds()
    {
        var sourceMap = new MarkdownSourceMap("embedded hosted");
        MarkdownLayoutContext context = CreateContext(sourceMap, new MarkdownExtensionRegistry());
        var embed = new EmbedBox(
            Markdown.Parse("embedded")[0],
            StubEmbedFactory.Instance)
        {
            BlockIndex = context.NextBlockIndex(),
        };

        var hostedBuilder = new MarkdownContentBuilder();
        hostedBuilder.AddHostedElement(
            "test.hosted",
            new SourceSpan(9, 6),
            MarkdownStyleRole.Body,
            semanticText: "hosted");
        MarkdownContentFragment hostedFragment = hostedBuilder.Build();
        var hosted = new DeclarativeHostedElementBox(
            context,
            Assert.Single(hostedFragment.Items),
            new HostedElementFallbackKey(hostedFragment))
        {
            BlockIndex = context.NextBlockIndex(),
        };

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { embed, hosted },
            sourceMap,
            width: 320,
            height: 120);
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;

        AssertBlockObjectEndpointsRoundTrip(
            semantic,
            embed.BlockIndex,
            Assert.Single(semantic.TextSpans, span => ReferenceEquals(span.EmbedBox, embed)));
        AssertBlockObjectEndpointsRoundTrip(
            semantic,
            hosted.BlockIndex,
            Assert.Single(semantic.TextSpans, span => ReferenceEquals(span.HostedElementBox, hosted)));
    }

    private static object InvokeRenderedSelectionPayload(LayoutSnapshot snapshot, DocumentRange range)
    {
#pragma warning disable MR1001 // Exercise the shared legacy copy pipeline without registering the obsolete control.
        MethodInfo method = typeof(MarkdownRendererControl).GetMethod(
            "GetRenderedSelectionPayload", PrivateStatic)
            ?? throw new InvalidOperationException("The control rendered-selection payload helper was not found.");
#pragma warning restore MR1001
        return method.Invoke(null, new object[] { snapshot, range })
            ?? throw new InvalidOperationException("The control returned no rendered-selection payload.");
    }

    private static string ReadPayloadField(object payload, string fieldName)
    {
        FieldInfo field = payload.GetType().GetField(fieldName, PrivateInstance)
            ?? throw new InvalidOperationException($"Rendered-selection payload field '{fieldName}' was not found.");
        return Assert.IsType<string>(field.GetValue(payload));
    }

    private static async Task<LayoutSnapshot> Build(string source)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme(SafeHtmlOptions.Default)
            .Build();
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(source);
        Assert.Empty(document.Diagnostics);

        MarkdownExtensionRegistry registry = Assert.IsType<MarkdownExtensionRegistry>(
            engine.PresentationConfiguration);
        var sourceMap = new MarkdownSourceMap(source);
        MarkdownLayoutContext context = CreateContext(sourceMap, registry);
        return new LayoutBuilder(context, semanticDocument: document)
            .Build(document.ParsedDocument!, 600);
    }

    private static MarkdownLayoutContext CreateContext(
        MarkdownSourceMap sourceMap,
        MarkdownExtensionRegistry registry)
    {
        var style = new ElementStyle();
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = style,
                [MarkdownElementKeys.ImageCaption] = style,
                [MarkdownElementKeys.CodeInline] = style,
                [MarkdownElementKeys.CodeBlock] = style,
            },
            new Dictionary<string, ElementStyleOverride>(),
            default(Color), default, default, default,
            isDark: false, isHighContrast: false, textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true),
            theme,
            sourceMap,
            registry,
            FlowDirection.LeftToRight);
    }

    private static void AssertBlockObjectEndpointsRoundTrip(
        MarkdownSemanticDocument semantic,
        int blockIndex,
        MarkdownTextSpan span)
    {
        var before = new DocumentPosition(blockIndex, 0, 0);
        var after = new DocumentPosition(blockIndex, 0, 1);
        Assert.Equal(span.TextStart, semantic.TextOffsetFromDocumentPosition(before));
        Assert.Equal(span.TextEnd, semantic.TextOffsetFromDocumentPosition(after));
        Assert.Equal((DocumentPosition?)before, semantic.PositionFromTextOffset(span.TextStart));
        Assert.Equal((DocumentPosition?)after, semantic.PositionFromTextOffset(span.TextEnd));
    }

    private sealed class StubEmbedFactory : IMarkdownEmbedFactory
    {
        internal static StubEmbedFactory Instance { get; } = new();

        public bool CanCreate(Markdig.Syntax.Block block) => true;
        public float MeasureHeight(Markdig.Syntax.Block block, float availableWidth) => 0;
        public FrameworkElement CreateBlock(Markdig.Syntax.Block block) => throw new NotSupportedException();
    }
}
