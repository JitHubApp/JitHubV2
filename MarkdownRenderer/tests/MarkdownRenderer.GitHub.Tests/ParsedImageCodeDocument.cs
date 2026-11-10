using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Markdig;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>A parsed GitHub README document with a completed raster image and a fenced code block, for code-block layout candidate tests.</summary>
internal sealed class ParsedImageCodeDocument : IAsyncDisposable
{
    private readonly MarkdownPerformanceSession _session;
    private bool _disposed;

    private ParsedImageCodeDocument(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        MarkdownLayoutContext context,
        LayoutSnapshot snapshot,
        CodeBlockPlacementRasterResolver resolver,
        ImageBox image,
        CodeBlockBox code)
    {
        Seed = seed;
        _session = session;
        Context = context;
        Snapshot = snapshot;
        Resolver = resolver;
        Image = image;
        Code = code;
        SourceMap = context.SourceMap;
    }

    internal CompletedRasterPlacementCloneTests.PlacementFixture Seed { get; }
    internal MarkdownLayoutContext Context { get; }
    internal LayoutSnapshot Snapshot { get; }
    internal CodeBlockPlacementRasterResolver Resolver { get; }
    internal ImageBox Image { get; }
    internal CodeBlockBox Code { get; }
    internal MarkdownSourceMap SourceMap { get; }

    internal static async Task<ParsedImageCodeDocument> CreateAsync(
        float rasterizationScale,
        bool rightToLeft,
        bool lazy,
        int spacerParagraphCount,
        bool singleLineCode = false,
        CodeBlockLineNumberMode lineNumberMode = CodeBlockLineNumberMode.Always)
    {
        var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownLayoutContext? context = null;
        LayoutSnapshot? snapshot = null;
        ParsedImageCodeDocument? ownedDocument = null;
        try
        {
            string sourceText = CreateMarkdown(spacerParagraphCount, singleLineCode);
            MarkdownExtensionRegistry registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
            Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(sourceText, registry.BuildPipeline());
            MarkdownDocument semanticDocument = MarkdownDocument.FromParsed(sourceText, parsed);
            var resolver = new CodeBlockPlacementRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(64, 32));
            context = CreateContext(
                seed, sourceText, registry, session, resolver, rightToLeft, rasterizationScale, lineNumberMode);

            var builder = new LayoutBuilder(context, semanticDocument: semanticDocument);
            snapshot = lazy
                ? builder.BuildLazy(parsed, CodeBlockLayoutCandidateTests.LayoutWidth, viewportTop: 0, viewportHeight: 120,
                    overscan: 0, CancellationToken.None)
                : builder.Build(parsed, CodeBlockLayoutCandidateTests.LayoutWidth, CancellationToken.None);
            snapshot.AssignAutomationDocumentGeneration(CodeBlockLayoutCandidateTests.AutomationGeneration);
            Assert.Null(snapshot.VectorSceneResidencyPolicy);
            ImageBox image = Assert.IsType<ImageBox>(snapshot.Blocks[0]);
            CodeBlockBox code = Assert.Single(snapshot.Blocks.OfType<CodeBlockBox>());
            ownedDocument = new ParsedImageCodeDocument(seed, session, context, snapshot, resolver, image, code);
            context = null;
            snapshot = null;

            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image)
                .WaitAsync(TimeSpan.FromSeconds(10));
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(
                () => image.GetWorkState().ActiveWorkCount == 0);
            Assert.NotNull(image.Bitmap);
            Assert.Equal(1, resolver.ResolveCount);
            Assert.True(code.HasCopyButtonAction);
            ParsedImageCodeDocument completedDocument = ownedDocument ??
                throw new InvalidOperationException("The parsed image/code fixture lost its owner.");
            ownedDocument = null;
            return completedDocument;
        }
        catch
        {
            if (ownedDocument is not null)
            {
                await ownedDocument.DisposeAsync();
                throw;
            }

            await ReleaseAsync(snapshot, context, session, seed);
            throw;
        }
    }

    private static MarkdownLayoutContext CreateContext(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        string sourceText,
        MarkdownExtensionRegistry registry,
        MarkdownPerformanceSession session,
        CodeBlockPlacementRasterResolver resolver,
        bool rightToLeft,
        float rasterizationScale,
        CodeBlockLineNumberMode lineNumberMode) =>
        new(
            seed.Device,
            seed.SourceContext.ThemeSnapshot,
            new MarkdownSourceMap(sourceText),
            registry,
            rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            language: "en-US")
        {
            RenderResources = seed.RenderResources,
            CancellationToken = CancellationToken.None,
            ImageCancellationToken = CancellationToken.None,
            ImagePreparationOwner = seed.ImagePreparationOwnerToken,
            PerformanceDocumentOwner = seed.PerformanceDocumentOwner,
            PerformanceSession = session,
            ImageResolver = resolver,
            AllowThirdPartyRemoteImages = true,
            RasterizationScale = rasterizationScale,
            CodeBlockLineNumberMode = lineNumberMode,
            CodeBlockChromeMode = CodeBlockChromeMode.Enhanced,
        };

    /// <summary>Retires the snapshot, drains the context, then disposes the session and seed, each step running even if an earlier one throws.</summary>
    private static async Task ReleaseAsync(
        LayoutSnapshot? snapshot,
        MarkdownLayoutContext? context,
        MarkdownPerformanceSession session,
        CompletedRasterPlacementCloneTests.PlacementFixture seed)
    {
        try
        {
            if (snapshot is not null)
                await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            try
            {
                if (context is not null)
                    await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                try
                {
                    session.Dispose();
                }
                finally
                {
                    await seed.DisposeAsync();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await ReleaseAsync(Snapshot, Context, _session, Seed);
    }

    private static string CreateMarkdown(int spacerParagraphCount, bool singleLineCode)
    {
        if (singleLineCode)
        {
            return "![local raster](https://images.example/code-layout.png)\n\n" +
                "```csharp\n" +
                // Markdig joins code lines without a terminal separator;
                // retain a blank authored line to exercise a parsed payload
                // whose final LF reaches code placement and copying.
                "let answer = 42;\n\n" +
                "```\n";
        }

        string longLiteral = new string('x', 480);
        string code =
            "public sealed class Example\n" +
            "{\n" +
            $"    public static string Value => \"{longLiteral}\";\n" +
            "    public int Read() => 42;\n" +
            "}\n";
        string spacer = string.Join("\n\n", Enumerable.Range(0, spacerParagraphCount)
            .Select(index => $"Far paragraph {index} keeps lazy roots beyond the first viewport."));
        return "![local raster](https://images.example/code-layout.png)\n\n" +
            (spacerParagraphCount == 0 ? string.Empty : spacer + "\n\n") +
            "```csharp filename=Example.cs startline=17 showLineNumbers\n" +
            code +
            "```\n";
    }
}
