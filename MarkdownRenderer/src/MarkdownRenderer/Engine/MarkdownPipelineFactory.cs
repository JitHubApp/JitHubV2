using System;
using System.Collections.Generic;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer;

/// <summary>Builds the Markdig pipeline a profile and extension set select.</summary>
internal static class MarkdownPipelineFactory
{
    internal static MarkdownPipeline Build(
        MarkdownProfile profile,
        MarkdownExtensionSet extensions,
        IReadOnlyList<Action<MarkdownPipelineBuilder>> pipelineConfigurations,
        bool enforceHtmlPolicy = true)
    {
        var features = profile.FeatureFlags;
        var builder = new MarkdownPipelineBuilder()
            .UsePreciseSourceLocation();

        if ((features & MarkdownProfileFeatures.PipeTables) != 0)
        {
            builder.UsePipeTables(new PipeTableOptions
            {
                UseHeaderForColumnCount = true,
            });
        }
        if ((features & MarkdownProfileFeatures.TaskLists) != 0)
            builder.UseTaskLists();
        if ((features & MarkdownProfileFeatures.AutoLinks) != 0)
            builder.UseAutoLinks();
        if ((features & MarkdownProfileFeatures.Strikethrough) != 0)
            builder.UseEmphasisExtras(EmphasisExtraOptions.Strikethrough);
        if ((features & MarkdownProfileFeatures.Footnotes) != 0)
            builder.UseFootnotes();
        if ((features & MarkdownProfileFeatures.Emoji) != 0)
            builder.UseEmojiAndSmiley(enableSmileys: false);
        if ((features & MarkdownProfileFeatures.DefinitionLists) != 0)
            builder.UseDefinitionLists();
        if ((features & MarkdownProfileFeatures.Abbreviations) != 0)
            builder.UseAbbreviations();
        if ((features & MarkdownProfileFeatures.Figures) != 0)
            builder.UseFigures();
        if ((features & MarkdownProfileFeatures.GenericAttributes) != 0)
            builder.UseGenericAttributes();
        if (extensions.HasFeature(MarkdownExtensionFeatures.DollarMath))
            builder.UseMathematics();

        // GitHub alerts intentionally remain quote blocks here. The native
        // GitHub feature pack recognizes their marker and contributes the
        // declarative renderer without making the core AST public.
        if (enforceHtmlPolicy && profile.HtmlMode == MarkdownHtmlMode.Literal)
            builder.DisableHtml();

        for (int index = 0; index < pipelineConfigurations.Count; index++)
            pipelineConfigurations[index](builder);

        return builder.Build();
    }
}
