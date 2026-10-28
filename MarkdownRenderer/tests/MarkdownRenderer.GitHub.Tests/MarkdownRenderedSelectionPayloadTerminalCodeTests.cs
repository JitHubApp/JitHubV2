using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Linq;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownRenderedSelectionPayloadTerminalCodeTests
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

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

        object payload = InvokeRenderedSelectionPayload(snapshot, range);
        Assert.Equal(expected, ReadPayloadField(payload, "Text"));

        string html = ReadPayloadField(payload, "Html");
        XElement root = XElement.Parse(html, LoadOptions.PreserveWhitespace);
        XElement code = Assert.Single(root.Descendants("code"));
        Assert.Equal(expected, code.Value);
        Assert.Equal(expected, root.Value);
    }

    private static object InvokeRenderedSelectionPayload(LayoutSnapshot snapshot, DocumentRange range)
    {
        MethodInfo method = typeof(MarkdownRendererControl).GetMethod(
            "GetRenderedSelectionPayload", PrivateStatic)
            ?? throw new InvalidOperationException("The control rendered-selection payload helper was not found.");
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
