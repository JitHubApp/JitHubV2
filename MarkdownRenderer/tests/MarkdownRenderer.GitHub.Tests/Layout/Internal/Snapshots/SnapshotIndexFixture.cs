using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Snapshots;

/// <summary>
/// A three-root box tree for <see cref="LayoutSnapshotIndexConstructionTests"/>
/// that exercises every snapshot index edge: shared and same-root image
/// aliases, duplicate block indexes, nested horizontal-overflow owners, code
/// chunks with links and images, focusable and decorative vector semantics,
/// hosted elements, block embeds and task-list markers.
/// </summary>
internal sealed record SnapshotIndexFixture(
    MarkdownLayoutContext Context,
    IReadOnlyList<BlockBox> Roots,
    ImageBox SharedImage,
    ImageBox SameRootImage,
    ImageBox CodeChunkImage,
    ImageBox TableCellImage,
    ImageBox DecorativeImage,
    InlineContainerBox CodeChunk,
    TableBox Table,
    IHorizontalOverflowBox OuterOverflowOwner,
    CodeBlockBox CopyCodeBlock,
    CodeBlockBox PlainMarkdownCodeBlock,
    VectorSceneBox FallbackVector,
    VectorSceneBox FocusableVector,
    DeclarativeHostedElementBox HostedElement,
    EmbedBox BlockEmbed,
    InlineContainerBox ReadOnlyTaskParagraph,
    InlineEmbedRun ReadOnlyTaskRun,
    int DuplicateTopLevelBlockIndex,
    int DuplicateOverflowBlockIndex,
    int NestedOverflowDescendantBlockIndex,
    int TableCellBlockIndex,
    IHorizontalOverflowBox LastDuplicateOverflowOwner)
{
    internal static SnapshotIndexFixture Create(
        MarkdownRenderResources renderResources,
        FlowDirection flowDirection)
    {
        MarkdownLayoutContext context = CreateContext(renderResources, flowDirection);
        var root = new StackBox { BlockIndex = 100 };

        InlineContainerBox inline = CreateMixedInline(
            context,
            out InlineImageRun sharedInlineImageRun,
            out InlineImageRun sameRootImageRun,
            out InlineEmbedRun readOnlyTask);
        root.Add(inline);

        // Repeat the same image identity within root zero, then under a
        // different root below. These aliases exercise the old identity index
        // ambiguity rule without loading or retaining a bitmap.
        root.Add(sharedInlineImageRun.Image);
        root.Add(sharedInlineImageRun.Image);
        root.Add(sameRootImageRun.Image);
        root.Add(sameRootImageRun.Image);

        TableBox table = CreateTable(context, out InlineImageRun tableCellImageRun);
        var outerOverflowOwner = new SnapshotIndexOverflowStackBox { BlockIndex = 190 };
        outerOverflowOwner.Add(NewParagraph(context, 199));
        outerOverflowOwner.Add(table);
        root.Add(outerOverflowOwner);

        CodeBlockBox copyCodeBlock = CreateCodeBlock(context, 200, isCopyButtonEnabled: true);
        InlineContainerBox codeChunk = CreateDenseCodeChunk(context, out InlineImageRun codeChunkImageRun);
        copyCodeBlock.AddChunk(codeChunk);
        root.Add(copyCodeBlock);

        CodeBlockBox plainMarkdownCodeBlock = CreatePlainMarkdownCodeBlock(renderResources, flowDirection);
        root.Add(plainMarkdownCodeBlock);

        VectorSceneBox fallbackVector = CreateVectorBox(context, 200, Array.Empty<MarkdownVectorSemanticItem>());
        root.Add(fallbackVector);

        VectorSceneBox focusableVector = CreateFocusableVector(context);
        root.Add(focusableVector);

        EmbedBox blockEmbed = CreateBlockEmbed();
        root.Add(blockEmbed);

        DeclarativeHostedElementBox hosted = CreateHostedElement(context);
        root.Add(hosted);
        root.Add(CreateTaskListItem(context, flowDirection));

        var secondRoot = new StackBox { BlockIndex = 100 };
        secondRoot.Add(sharedInlineImageRun.Image);
        ImageBox decorativeImage = CreateDecorativeImage(context);

        return new SnapshotIndexFixture(
            context,
            new BlockBox[] { root, secondRoot, decorativeImage },
            sharedInlineImageRun.Image,
            sameRootImageRun.Image,
            codeChunkImageRun.Image,
            tableCellImageRun.Image,
            decorativeImage,
            codeChunk,
            table,
            outerOverflowOwner,
            copyCodeBlock,
            plainMarkdownCodeBlock,
            fallbackVector,
            focusableVector,
            hosted,
            blockEmbed,
            inline,
            readOnlyTask,
            DuplicateTopLevelBlockIndex: 100,
            DuplicateOverflowBlockIndex: 200,
            NestedOverflowDescendantBlockIndex: 199,
            TableCellBlockIndex: 201,
            LastDuplicateOverflowOwner: focusableVector);
    }

    private static CodeBlockBox CreatePlainMarkdownCodeBlock(
        MarkdownRenderResources renderResources,
        FlowDirection flowDirection)
    {
        MarkdownLayoutContext plainContext = CreateContext(
            renderResources,
            flowDirection,
            CodeBlockChromeMode.PlainMarkdown);
        return CreateCodeBlock(
            plainContext,
            203,
            isCopyButtonEnabled: true);
    }

    private static VectorSceneBox CreateFocusableVector(MarkdownLayoutContext context)
    {
        MarkdownVectorSemanticItem[] vectorSemantics =
        [
            CreateSemantic(1, MarkdownVectorSemanticFlags.Focusable),
            CreateSemantic(2, MarkdownVectorSemanticFlags.Focusable | MarkdownVectorSemanticFlags.Decorative),
        ];
        return CreateVectorBox(context, 200, vectorSemantics);
    }

    private static EmbedBox CreateBlockEmbed() =>
        new(Markdig.Markdown.Parse("embedded block")[0], SnapshotIndexEmbedFactory.Instance)
        {
            BlockIndex = 205,
        };

    private static ImageBox CreateDecorativeImage(MarkdownLayoutContext context) =>
        new(context, context.ImageResources, "https://example.test/decorative.png", string.Empty)
        {
            BlockIndex = 220,
        };

    internal static MarkdownLayoutContext CreateContext(
        MarkdownRenderResources renderResources,
        FlowDirection flowDirection,
        CodeBlockChromeMode codeBlockChromeMode = CodeBlockChromeMode.Enhanced)
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.ListMarker] = style,
            [MarkdownElementKeys.TableCell] = style,
            [MarkdownElementKeys.CodeBlock] = style,
            [MarkdownElementKeys.Math] = style,
            [MarkdownElementKeys.Diagram] = style,
            [MarkdownElementKeys.Link] = style,
        };
        Color black = Color.FromArgb(255, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            black,
            black,
            black,
            black,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(new string('x', 4096)),
            new MarkdownExtensionRegistry(),
            flowDirection)
        {
            RenderResources = renderResources,
            CodeBlockChromeMode = codeBlockChromeMode,
        };
    }

    internal static InlineContainerBox NewParagraph(
        MarkdownLayoutContext context,
        int blockIndex,
        string? elementKey = null)
        => new(context, elementKey ?? MarkdownElementKeys.Body)
        {
            BlockIndex = blockIndex,
        };

    private static InlineContainerBox CreateMixedInline(
        MarkdownLayoutContext context,
        out InlineImageRun sharedInlineImageRun,
        out InlineImageRun sameRootImageRun,
        out InlineEmbedRun readOnlyTask)
    {
        var inline = NewParagraph(context, blockIndex: 101);
        inline.Add(new TextRun("text before"));
        inline.Add(new LinkRun("linked text", "https://example.test/link"));
        sharedInlineImageRun = new InlineImageRun(context, "shared", "https://example.test/shared.png");
        inline.Add(sharedInlineImageRun);
        sameRootImageRun = new InlineImageRun(context, "same root", "https://example.test/same-root.png");
        inline.Add(sameRootImageRun);
        inline.Add(new InlineImageRun(context, string.Empty, "https://example.test/decorative-inline.png"));
        readOnlyTask = new InlineEmbedRun(18, 18, static () => null!)
        {
            AutomationMetadata = new InlineEmbedAutomationMetadata(
                "Complete",
                "Incomplete",
                "Read only",
                "Toggle",
                "task-readonly",
                isChecked: false),
        };
        inline.Add(readOnlyTask);
        inline.Add(new InlineEmbedRun(18, 18, static () => null!));
        var linkedInlineVector = new InlineVectorSceneRun(
            context,
            CreateVectorScene(Array.Empty<MarkdownVectorSemanticItem>()),
            "math",
            "math",
            accessibilityDescription: null,
            elementKey: MarkdownElementKeys.Math)
        {
            LinkUrl = "https://example.test/math",
        };
        inline.Add(linkedInlineVector);
        return inline;
    }

    private static TableBox CreateTable(MarkdownLayoutContext context, out InlineImageRun tableCellImageRun)
    {
        var tableHeaderLink = NewParagraph(context, 201, MarkdownElementKeys.TableCell);
        tableHeaderLink.Add(new LinkRun("table link", "https://example.test/table"));
        var secondTableHeaderLink = NewParagraph(context, 211, MarkdownElementKeys.TableCell);
        secondTableHeaderLink.Add(new LinkRun("second header link", "https://example.test/table-header-2"));
        var tableBodyTask = NewParagraph(context, 202, MarkdownElementKeys.TableCell);
        tableBodyTask.Add(new InlineEmbedRun(16, 16, () => null!)
        {
            AutomationMetadata = new InlineEmbedAutomationMetadata(
                "Done",
                "Not done",
                "Read only task",
                "Toggle",
                "task-table",
                isChecked: true),
        });
        var secondTableBodyLink = NewParagraph(context, 212, MarkdownElementKeys.TableCell);
        secondTableBodyLink.Add(new LinkRun("body link", "https://example.test/table-body"));
        var laterTableCell = NewParagraph(context, 213, MarkdownElementKeys.TableCell);
        laterTableCell.Add(new TextRun("later row"));
        tableCellImageRun = new InlineImageRun(context, "table cell image", "https://example.test/table-cell.png");
        laterTableCell.Add(tableCellImageRun);
        return new TableBox(
            context,
            new[] { new[] { tableHeaderLink, secondTableHeaderLink } },
            new[] { new[] { tableBodyTask, secondTableBodyLink }, new[] { laterTableCell } })
        {
            BlockIndex = 200,
        };
    }

    private static InlineContainerBox CreateDenseCodeChunk(
        MarkdownLayoutContext context,
        out InlineImageRun codeChunkImageRun)
    {
        var codeChunk = NewParagraph(context, 202, MarkdownElementKeys.CodeBlock);
        for (int index = 0; index < 128; index++)
            codeChunk.Add(new TextRun($"syntax-{index}"));
        codeChunk.Add(new LinkRun("chunk link must not focus", "https://example.test/chunk"));
        codeChunkImageRun = new InlineImageRun(context, "code image", "https://example.test/code.png");
        codeChunk.Add(codeChunkImageRun);
        return codeChunk;
    }

    private static DeclarativeHostedElementBox CreateHostedElement(MarkdownLayoutContext context)
    {
        var hostedBuilder = new MarkdownContentBuilder();
        hostedBuilder.AddHostedElement(
            "test.hosted",
            new SourceSpan(0, 1),
            MarkdownStyleRole.Body,
            MarkdownAccessibilityRole.Group,
            accessibilityName: "Hosted region");
        MarkdownContentFragment hostedFragment = hostedBuilder.Build();
        return new DeclarativeHostedElementBox(
            context,
            Assert.Single(hostedFragment.Items),
            new HostedElementFallbackKey(hostedFragment))
        {
            BlockIndex = 206,
        };
    }

    private static ListItemBox CreateTaskListItem(MarkdownLayoutContext context, FlowDirection flowDirection)
    {
        var marker = NewParagraph(context, 208, MarkdownElementKeys.ListMarker);
        marker.Add(new InlineEmbedRun(14, 14, () => null!)
        {
            AutomationMetadata = new InlineEmbedAutomationMetadata(
                "Checked",
                "Unchecked",
                "Task state",
                "Toggle task",
                "task-list-marker",
                isChecked: true),
        });
        var listContent = new StackBox { BlockIndex = 209 };
        var listParagraph = NewParagraph(context, 210);
        listParagraph.Add(new LinkRun("list link", "https://example.test/list"));
        listContent.Add(listParagraph);
        return new ListItemBox(marker, listContent, markerWidth: 22)
        {
            FlowDirection = flowDirection,
            BlockIndex = 207,
        };
    }

    private static CodeBlockBox CreateCodeBlock(
        MarkdownLayoutContext context,
        int blockIndex,
        bool isCopyButtonEnabled)
    {
        const string code = "var answer = 42;";
        CodeBlockMetadata metadata = CodeBlockMetadata.FromDeclarative(
            new SourceSpan(0, code.Length),
            code,
            "csharp",
            new Dictionary<string, string>());
        return new CodeBlockBox(context, metadata, code, isCopyButtonEnabled, showLineNumbers: false)
        {
            BlockIndex = blockIndex,
        };
    }

    private static VectorSceneBox CreateVectorBox(
        MarkdownLayoutContext context,
        int blockIndex,
        IReadOnlyList<MarkdownVectorSemanticItem> semantics)
    {
        var builder = new MarkdownContentBuilder();
        builder.AddVectorScene(
            CreateVectorScene(semantics),
            new SourceSpan(0, 1),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "Test diagram");
        MarkdownContentFragment fragment = builder.Build();
        return new VectorSceneBox(context, Assert.Single(fragment.Items), MarkdownElementKeys.Diagram)
        {
            BlockIndex = blockIndex,
        };
    }

    private static MarkdownVectorScene CreateVectorScene(
        IReadOnlyList<MarkdownVectorSemanticItem> semantics)
        => new(
            width: 24,
            height: 12,
            baseline: 10,
            commands: Array.Empty<MarkdownVectorCommand>(),
            referenceFontSize: 12,
            viewport: new MarkdownVectorRectangle(0, 0, 24, 12),
            semantics: semantics,
            links: Array.Empty<MarkdownVectorLinkAction>());

    private static MarkdownVectorSemanticItem CreateSemantic(
        uint sourceId,
        MarkdownVectorSemanticFlags flags)
        => new(
            sourceId: sourceId,
            role: MarkdownVectorSemanticRole.Node,
            name: $"node {sourceId}",
            description: null,
            parentIndex: -1,
            sourceSpan: new SourceSpan(checked((int)sourceId), 1),
            bounds: new MarkdownVectorRectangle(0, 0, 8, 6),
            flags: flags);
}
