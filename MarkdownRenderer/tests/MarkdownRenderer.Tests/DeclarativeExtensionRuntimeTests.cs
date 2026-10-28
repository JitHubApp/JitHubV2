using MarkdownRenderer.Extensions;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Theming;
using Markdig.Syntax;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class DeclarativeExtensionRuntimeTests
{
    [Fact]
    public async Task EngineDispatchesParagraphByStableExactSyntaxKind()
    {
        string? observedKind = null;
        string? observedLiteral = null;
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new TestExtension(builder =>
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, content) =>
                {
                    observedKind = context.Node.Kind;
                    observedLiteral = context.Node.Literal;
                    content.AddHostedElement(
                        "tests.card",
                        context.Node.SourceSpan,
                        MarkdownStyleRole.Body,
                        attributes: new Dictionary<string, string>
                        {
                            [MarkdownHostedElementAttributes.DesiredHeight] = "72",
                            ["label"] = "example",
                        },
                        accessibilityName: "Example card",
                        accessibilityDescription: "A host-created example card.",
                        semanticText: "Example card value");
                })))
            .Build();

        const string source = "A declarative card.";
        var document = await engine.ParseAsync(source);

        Assert.Equal(MarkdownSyntaxKinds.Block.Paragraph, observedKind);
        Assert.Equal(source, observedLiteral);
        Assert.True(document.TryGetExtensionContent(new SourceSpan(0, source.Length), out var fragment));
        var hosted = Assert.Single(fragment!.Items);
        Assert.Equal(MarkdownContentKind.HostedElement, hosted.Kind);
        Assert.Equal("tests.card", hosted.FactoryKey);
        Assert.Equal("72", hosted.Attributes[MarkdownHostedElementAttributes.DesiredHeight]);
        Assert.Equal("example", hosted.Attributes["label"]);
        Assert.Equal(MarkdownAccessibilityRole.Group, hosted.AccessibilityRole);
        Assert.Equal("Example card", hosted.AccessibilityName);
        Assert.Equal("A host-created example card.", hosted.AccessibilityDescription);
        Assert.Equal("Example card value", hosted.SemanticText);
    }

    [Fact]
    public async Task ExactTypeDispatchDoesNotTreatFencedCodeAsIndentedCode()
    {
        int invocations = 0;
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new TestExtension(builder =>
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.IndentedCode, (_, content) =>
                {
                    invocations++;
                    content.AddHostedElement(
                        "tests.code",
                        SourceSpan.Empty,
                        MarkdownStyleRole.CodeBlock);
                })))
            .Build();

        var document = await engine.ParseAsync("```text\nhello\n```");

        Assert.Equal(0, invocations);
        Assert.False(document.TryGetExtensionContent(new SourceSpan(0, document.Source.Length), out _));
    }

    [Fact]
    public async Task ReusableDocumentRetainsFrozenExtensionOutput()
    {
        var mutable = new Dictionary<string, string> { ["version"] = "one" };
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new TestExtension(builder =>
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, content) =>
                    content.AddHostedElement(
                        "tests.reusable",
                        context.Node.SourceSpan,
                        MarkdownStyleRole.Body,
                        attributes: mutable))))
            .Build();

        const string source = "Reusable";
        var document = await engine.ParseAsync(source);
        mutable["version"] = "two";

        Assert.True(document.TryGetExtensionContent(new SourceSpan(0, source.Length), out var fragment));
        Assert.Equal("one", Assert.Single(fragment!.Items).Attributes["version"]);
    }

    [Fact]
    public async Task EngineDispatchesAndRetainsInlineOutputByExactSyntaxKind()
    {
        int invocations = 0;
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new TestExtension(builder =>
                builder.RegisterInline(MarkdownSyntaxKinds.Inline.Text, (context, content) =>
                {
                    invocations++;
                    content.AddText(
                        context.Node.Literal ?? string.Empty,
                        context.Node.SourceSpan,
                        MarkdownStyleRole.Body);
                })))
            .Build();

        const string source = "Inline extension";
        var document = await engine.ParseAsync(source);

        Assert.Equal(1, invocations);
        Assert.True(document.TryGetInlineExtensionContent(
            new SourceSpan(0, source.Length),
            out var fragment));
        var text = Assert.Single(fragment!.Items);
        Assert.Equal(MarkdownContentKind.Text, text.Kind);
        Assert.Equal(source, text.Text);
        Assert.False(document.TryGetBlockExtensionContent(
            new SourceSpan(0, source.Length),
            out _));
    }

    [Fact]
    public async Task WholeSpanInlineHostedOutputIsNeverSelectedAsBlockOutput()
    {
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new TestExtension(builder =>
                builder.RegisterInline(MarkdownSyntaxKinds.Inline.Text, (context, content) =>
                    content.AddHostedElement(
                        "tests.inline",
                        context.Node.SourceSpan,
                        MarkdownStyleRole.Body))))
            .Build();

        var document = await engine.ParseAsync("whole span");
        Block paragraph = Assert.Single(document.ParsedDocument!);

        Assert.False(DeclarativeBlockSelection.TryGetContent(
            document,
            paragraph,
            new HashSet<HostedElementFallbackKey>(),
            out _));
    }

    [Theory]
    [InlineData("- one", 1)]
    [InlineData("- one\n- two", 2)]
    public async Task ListItemOutputUsesExactNodeIdentityForEveryItem(
        string source,
        int expectedItemCount)
    {
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new TestExtension(builder =>
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.ListItem, (context, content) =>
                    content.AddHostedElement(
                        "tests.list-item",
                        context.Node.SourceSpan,
                        MarkdownStyleRole.Body))))
            .Build();

        var document = await engine.ParseAsync(source);
        var list = Assert.IsType<ListBlock>(Assert.Single(document.ParsedDocument!));
        var items = list.OfType<ListItemBlock>().ToArray();
        Assert.Equal(expectedItemCount, items.Length);
        if (items.Length == 1)
            Assert.Equal(list.Span, items[0].Span);

        var fallbacks = new HashSet<HostedElementFallbackKey>();
        Assert.False(DeclarativeBlockSelection.TryGetContent(
            document,
            list,
            fallbacks,
            out _));
        foreach (ListItemBlock item in items)
        {
            Assert.True(DeclarativeBlockSelection.TryGetContent(
                document,
                item,
                fallbacks,
                out var fragment));
            Assert.Equal("tests.list-item", Assert.Single(fragment!.Items).FactoryKey);
        }
    }

    [Fact]
    public async Task FailedHostedFactoryKeySelectsNativeMarkdownFallbackOnlyForThatBlock()
    {
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new TestExtension(builder =>
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, content) =>
                    content.AddHostedElement(
                        $"tests.{context.Node.Literal}",
                        context.Node.SourceSpan,
                        MarkdownStyleRole.Body))))
            .Build();

        var document = await engine.ParseAsync("supported\n\nunsupported");
        Block[] blocks = document.ParsedDocument!.ToArray();
        Assert.Equal(2, blocks.Length);
        Assert.True(document.TryGetBlockExtensionContent(blocks[1], out var failedFragment));
        MarkdownContent failed = Assert.Single(failedFragment!.Items);
        var fallbacks = new HashSet<HostedElementFallbackKey>
        {
            new(failedFragment),
        };

        Assert.True(DeclarativeBlockSelection.TryGetContent(
            document,
            blocks[0],
            fallbacks,
            out _));
        Assert.False(DeclarativeBlockSelection.TryGetContent(
            document,
            blocks[1],
            fallbacks,
            out _));
    }

    [Fact]
    public async Task FailedOuterListFragmentDoesNotSuppressCollidingListItemFragment()
    {
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new TestExtension(builder =>
            {
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.List, (context, content) =>
                    content.AddHostedElement(
                        "tests.shared",
                        context.Node.SourceSpan,
                        MarkdownStyleRole.Body));
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.ListItem, (context, content) =>
                    content.AddHostedElement(
                        "tests.shared",
                        context.Node.SourceSpan,
                        MarkdownStyleRole.Body));
            }))
            .Build();

        var document = await engine.ParseAsync("- one");
        var list = Assert.IsType<ListBlock>(Assert.Single(document.ParsedDocument!));
        var item = Assert.IsType<ListItemBlock>(Assert.Single(list));
        Assert.Equal(list.Span, item.Span);
        Assert.True(document.TryGetBlockExtensionContent(list, out var listFragment));
        IReadOnlyList<MarkdownContentFragment> collidingFragments =
            document.GetBlockExtensionContent(new SourceSpan(list.Span.Start, list.Span.Length));
        Assert.Equal(2, collidingFragments.Count);
        Assert.Same(listFragment, collidingFragments[0]);

        var fallbacks = new HashSet<HostedElementFallbackKey>
        {
            new(listFragment!),
        };

        Assert.False(DeclarativeBlockSelection.TryGetContent(
            document,
            list,
            fallbacks,
            out _));
        Assert.True(DeclarativeBlockSelection.TryGetContent(
            document,
            item,
            fallbacks,
            out var itemFragment));
        Assert.Equal("tests.shared", Assert.Single(itemFragment!.Items).FactoryKey);
    }

    [Fact]
    public async Task OrderedHandlersContinueUntilOneEmitsContent()
    {
        var invocations = new List<string>();
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new TestExtension(builder =>
            {
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.FencedCode, (_, _) =>
                    invocations.Add("ignored"));
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.FencedCode, (context, content) =>
                {
                    invocations.Add("handled");
                    content.AddCodeBlock(
                        context.Node.Literal ?? string.Empty,
                        "handled",
                        context.Node.SourceSpan);
                });
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.FencedCode, (_, _) =>
                    invocations.Add("must-not-run"));
            }))
            .Build();

        var document = await engine.ParseAsync("```text\nvalue\n```");

        Assert.Equal(["ignored", "handled"], invocations);
        MarkdownCodeBlock codeBlock = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(codeBlock.SourceSpan, out MarkdownContentFragment? fragment));
        Assert.Equal("handled", Assert.Single(fragment!.Items).Language);
    }

    [Fact]
    public async Task OrderedHandlersStopAfterDiagnosticEmission()
    {
        int secondInvocations = 0;
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new TestExtension(builder =>
            {
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, _) =>
                    context.ReportDiagnostic(
                        "TEST0001",
                        MarkdownDiagnosticSeverity.Warning,
                        "The first handler claimed this node.",
                        context.Node.SourceSpan));
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (_, _) =>
                    secondInvocations++);
            }))
            .Build();

        var document = await engine.ParseAsync("claimed");

        Assert.Equal(0, secondInvocations);
        Assert.Contains(document.Diagnostics, static diagnostic => diagnostic.Code == "TEST0001");
    }

    [Fact]
    public async Task InlineHandlersUseTheSameOrderedStopAfterEmissionContract()
    {
        var invocations = new List<string>();
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new TestExtension(builder =>
            {
                builder.RegisterInline(MarkdownSyntaxKinds.Inline.Text, (_, _) =>
                    invocations.Add("ignored"));
                builder.RegisterInline(MarkdownSyntaxKinds.Inline.Text, (context, content) =>
                {
                    invocations.Add("handled");
                    content.AddText(
                        context.Node.Literal ?? string.Empty,
                        context.Node.SourceSpan,
                        MarkdownStyleRole.Body);
                });
                builder.RegisterInline(MarkdownSyntaxKinds.Inline.Text, (_, _) =>
                    invocations.Add("must-not-run"));
            }))
            .Build();

        await engine.ParseAsync("inline");

        Assert.Equal(["ignored", "handled"], invocations);
    }

    private sealed class TestExtension(Action<MarkdownExtensionBuilder> configure) : IMarkdownExtension
    {
        public string Id => "MarkdownRenderer.Tests.DeclarativeRuntime";

        public void Configure(MarkdownExtensionBuilder builder) => configure(builder);
    }
}
