using System.Collections.Generic;
using System.Threading.Tasks;
using System.Xml.Linq;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Internal.Selection;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Layout.Internal.Building;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Controls.Internal.Selection;

public sealed class MarkdownRenderedSelectionPayloadTerminalCodeTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task ControlSelectionPayloadRoundTripsAuthoredTerminalCodeLineFeed(string lineEnding)
    {
        string source = $"```text{lineEnding}alpha{lineEnding}{lineEnding}```";
        const string expected = "alpha\n";

        using LayoutSnapshot snapshot = await Build(source);
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        Assert.Equal(expected, semantic.Text);
        Assert.True(semantic.TryGetDocumentRange(0, semantic.Text.Length, out DocumentRange range));

        // This is the exact reverse mapping used by GetSelection and by the
        // control's rendered-copy payload. It proves the final UTF-16 LF is
        // still inside the document-position range after the source newline
        // convention has normalized to the renderer's LF semantic text.
        Assert.Equal(0, semantic.TextOffsetFromDocumentPosition(range.Start));
        Assert.Equal(semantic.Text.Length, semantic.TextOffsetFromDocumentPosition(range.End));

        RenderedSelectionPayload payload = SelectionCommands.GetRenderedSelectionPayload(snapshot, range);
        Assert.Equal(expected, payload.Text);

        string html = payload.Html;
        XElement root = XElement.Parse(html, LoadOptions.PreserveWhitespace);
        XElement code = Assert.Single(root.Descendants("code"));
        Assert.Equal(expected, code.Value);
        Assert.Equal(expected, root.Value);
    }

    private static async Task<LayoutSnapshot> Build(string source)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme(SafeHtmlOptions.Default)
            .Build();
        MarkdownDocument document = await engine.ParseAsync(source);
        MarkdownExtensionRegistry registry = Assert.IsType<MarkdownExtensionRegistry>(
            engine.PresentationConfiguration);
        var style = new ElementStyle();
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = style,
                [MarkdownElementKeys.CodeInline] = style,
                [MarkdownElementKeys.CodeBlock] = style,
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
        return new LayoutBuilder(context, semanticDocument: document)
            .Build(document.ParsedDocument!, 600);
    }
}
