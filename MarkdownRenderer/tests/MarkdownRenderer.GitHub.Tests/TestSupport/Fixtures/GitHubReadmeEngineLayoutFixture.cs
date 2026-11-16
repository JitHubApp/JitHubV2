using MarkdownRenderer.GitHub;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;

/// <summary>
/// Parses source through the public GitHub README engine, as the app does,
/// lays the parsed document out with that engine's presentation registry, and
/// reads back every inline container (including list items and table cells).
/// </summary>
internal static class GitHubReadmeEngineLayoutFixture
{
    internal static async Task<LayoutSnapshot> BuildAsync(
        string source,
        IReadOnlyDictionary<string, ElementStyle>? keyedStyles = null)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(source);
        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        return SafeHtmlRenderingFixture.Build(
            source,
            registry,
            parsedDocument: document.ParsedDocument,
            keyedStyles: keyedStyles);
    }

    internal static IEnumerable<InlineContainerBox> EnumerateInlineBoxes(LayoutSnapshot snapshot)
    {
        var pending = new Stack<BlockBox>(snapshot.Blocks.Reverse());
        while (pending.Count > 0)
        {
            BlockBox block = pending.Pop();
            switch (block)
            {
                case InlineContainerBox inline:
                    yield return inline;
                    break;
                case StackBox stack:
                    for (int index = stack.Children.Count - 1; index >= 0; index--)
                        pending.Push(stack.Children[index]);
                    break;
                case ListItemBox listItem:
                    pending.Push(listItem.Content);
                    pending.Push(listItem.Marker);
                    break;
                case TableBox table:
                    foreach (InlineContainerBox cell in table.GetCellBoxes().Reverse())
                        pending.Push(cell);
                    break;
            }
        }
    }

    internal static string GetText(InlineContainerBox box)
        => string.Concat(box.Runs.Select(static run => run.Text));
}
