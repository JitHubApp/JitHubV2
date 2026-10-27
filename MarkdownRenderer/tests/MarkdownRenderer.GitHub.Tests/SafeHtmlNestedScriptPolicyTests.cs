using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SafeHtmlNestedScriptPolicyTests
{
    [Theory]
    [InlineData("<custom>visible<script>hidden()</script></custom>", "visible")]
    [InlineData("<p><custom>visible<script>hidden()</script></custom></p>", "visible")]
    [InlineData("<p><span>before<script>hidden()</script>after</span></p>", "beforeafter")]
    [InlineData("<div><p><custom>before<script>hidden()</script>after</custom></p></div>", "beforeafter")]
    public async Task KeepTextContentSuppressesNestedScriptMarkupAndContent(
        string source,
        string expectedText)
    {
        var options = new SafeHtmlOptions(
            unknownElementBehavior: SafeHtmlUnknownElementBehavior.KeepTextContent);
        using LayoutSnapshot snapshot = await Build(source, options);

        Assert.Equal(expectedText, string.Concat(Runs(snapshot).Select(static run => run.Text)));
    }

    [Fact]
    public async Task RenderLiteralKeepsScriptSourceAsInertTextInsideSupportedParent()
    {
        const string rawScript = "<script>unsafe-script-sentinel()</script>";
        const string source = "<p>before" + rawScript + "after</p>";
        using LayoutSnapshot snapshot = await Build(source, SafeHtmlOptions.Default);

        Assert.Equal("before" + rawScript + "after", string.Concat(Runs(snapshot).Select(static run => run.Text)));
        Assert.All(
            Runs(snapshot).Where(run => run.SourceSpan.Start >= source.IndexOf(rawScript, StringComparison.Ordinal) &&
                run.SourceSpan.Start < source.IndexOf(rawScript, StringComparison.Ordinal) + rawScript.Length),
            static run => Assert.IsType<TextRun>(run));
    }

    private static async Task<LayoutSnapshot> Build(string source, SafeHtmlOptions options)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme(options)
            .Build();
        MarkdownDocument semanticDocument = await engine.ParseAsync(source);
        Markdig.Syntax.MarkdownDocument document = semanticDocument.ParsedDocument ??
            throw new InvalidOperationException("The engine did not retain its parsed document.");
        MarkdownExtensionRegistry registry = Assert.IsType<MarkdownExtensionRegistry>(
            engine.PresentationConfiguration);
        var style = new ElementStyle();
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = style,
                [MarkdownElementKeys.Link] = style,
                [MarkdownElementKeys.ImageCaption] = style,
            },
            new Dictionary<string, ElementStyleOverride>(),
            default(Color), default, default, default,
            isDark: false, isHighContrast: false, textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight);
        return new LayoutBuilder(context, semanticDocument: semanticDocument).Build(document, 600);
    }

    private static IEnumerable<InlineRun> Runs(LayoutSnapshot snapshot) =>
        snapshot.Blocks.SelectMany(Runs);

    private static IEnumerable<InlineRun> Runs(BlockBox block) => block switch
    {
        InlineContainerBox inline => inline.Runs,
        StackBox stack => stack.Children.SelectMany(Runs),
        _ => [],
    };
}
