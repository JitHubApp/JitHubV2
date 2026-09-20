using Markdig;
using MarkdownRenderer.Images;
using MarkdownRenderer.Parsing;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownImagePrefetchSourceCollectorTests
{
    [Fact]
    public void Collect_DeduplicatesRenderedMarkdownAndSafeHtmlImages()
    {
        const string source = """
            ![one](https://images.example/one.png)
            ![duplicate](https://images.example/one.png)

            <img src="https://images.example/two.png" alt="two">

            ```markdown
            ![code](https://images.example/code.png)
            ```
            """;
        var document = Markdown.Parse(source, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        SafeHtmlRenderPolicy policy = new(
            enableLinks: true,
            enableImages: true,
            renderUnknownElementsLiterally: false,
            allowedStyleClasses: null);

        IReadOnlyList<string> images = MarkdownImagePrefetchSourceCollector.Collect(
            document,
            policy,
            CancellationToken.None);

        Assert.Equal(
            ["https://images.example/one.png", "https://images.example/two.png"],
            images);
    }

    [Fact]
    public void Collect_DoesNotPrefetchSuppressedHtmlContent()
    {
        const string source = """
            <script>
            <img src="https://images.example/script.png">
            </script>

            <template><img src="https://images.example/template.png"></template>

            ![visible](https://images.example/visible.png)
            """;
        var document = Markdown.Parse(source, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        SafeHtmlRenderPolicy policy = new(
            enableLinks: true,
            enableImages: true,
            renderUnknownElementsLiterally: false,
            allowedStyleClasses: null);

        IReadOnlyList<string> images = MarkdownImagePrefetchSourceCollector.Collect(
            document,
            policy,
            CancellationToken.None);

        Assert.Equal(["https://images.example/visible.png"], images);
    }
}
