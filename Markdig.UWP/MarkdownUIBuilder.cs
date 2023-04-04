using Markdig.UWP.Renderers;
using Markdig.UWP.TextElements;
using System;
using Windows.UI.Xaml;

namespace Markdig.UWP;

public static class MarkdownUIBuilder
{
    public static UIElement Build(MarkdownConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        var pipeline = new MarkdownPipelineBuilder()
            .UseEmphasisExtras()
            .UseAutoLinks()
            .UseTaskLists()
            .UsePipeTables()
            .Build();

        var result = new MyFlowDocument();
        var renderer = new UWPRenderer(result, config);

        pipeline.Setup(renderer);

        var document = Markdown.Parse(config.Markdown, pipeline);
        renderer.Render(document);

        return result.RichTextBlock;
    }
}
