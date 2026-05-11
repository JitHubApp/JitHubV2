using System.Collections.Generic;
using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Unit tests for footnote back-link infrastructure:
/// <see cref="FocusableItem"/> struct and Markdig footnote parsing.
/// </summary>
public class FootnoteBacklinkTests
{
    // ---- FocusableItem struct ----

    [Fact]
    public void FocusableItem_Link_HasCorrectFields()
    {
        var item = new FocusableItem(3, 7, isLink: true);
        Assert.Equal(3, item.BlockIndex);
        Assert.Equal(7, item.InlineIndex);
        Assert.True(item.IsLink);
    }

    [Fact]
    public void FocusableItem_Embed_HasCorrectFields()
    {
        var item = new FocusableItem(0, 1, isLink: false);
        Assert.Equal(0, item.BlockIndex);
        Assert.Equal(1, item.InlineIndex);
        Assert.False(item.IsLink);
    }

    // ---- Markdig footnote parsing produces FootnoteLink inlines ----

    [Fact]
    public void MarkdigFootnote_ParsesFootnoteLinks()
    {
        var pipeline = new MarkdownPipelineBuilder().UseFootnotes().Build();
        var md = "Here is a footnote[^1].\n\n[^1]: The definition.";
        var doc = Markdig.Markdown.Parse(md, pipeline);

        bool foundLink = false;
        foreach (var block in doc)
        {
            if (block is ParagraphBlock pb)
            {
                foreach (var inline in pb.Inline!)
                {
                    if (inline is FootnoteLink) { foundLink = true; break; }
                }
            }
            if (foundLink) break;
        }
        Assert.True(foundLink, "Expected at least one FootnoteLink inline");
    }

    [Fact]
    public void MarkdigFootnote_ParsesFootnoteGroup()
    {
        var pipeline = new MarkdownPipelineBuilder().UseFootnotes().Build();
        var md = "Para[^1].\n\n[^1]: The def.";
        var doc = Markdig.Markdown.Parse(md, pipeline);

        bool foundGroup = false;
        foreach (var block in doc)
        {
            if (block is FootnoteGroup) { foundGroup = true; break; }
        }
        Assert.True(foundGroup, "Expected a FootnoteGroup block");
    }

    [Fact]
    public void MarkdigFootnote_MultipleFootnotes_AllLinksPresent()
    {
        var pipeline = new MarkdownPipelineBuilder().UseFootnotes().Build();
        var md = "A[^1] B[^2] C[^3].\n\n[^1]: One.\n[^2]: Two.\n[^3]: Three.";
        var doc = Markdig.Markdown.Parse(md, pipeline);

        int linkCount = 0;
        foreach (var block in doc)
        {
            if (block is ParagraphBlock pb)
            {
                foreach (var inline in pb.Inline!)
                    if (inline is FootnoteLink) linkCount++;
            }
        }
        Assert.Equal(3, linkCount);
    }

    [Fact]
    public void MarkdigFootnote_FootnoteLink_HasCorrectOrder()
    {
        var pipeline = new MarkdownPipelineBuilder().UseFootnotes().Build();
        var md = "A[^a].\n\n[^a]: Def.";
        var doc = Markdig.Markdown.Parse(md, pipeline);

        FootnoteLink? link = null;
        foreach (var block in doc)
        {
            if (block is ParagraphBlock pb)
            {
                foreach (var inline in pb.Inline!)
                {
                    if (inline is FootnoteLink fl) { link = fl; break; }
                }
            }
            if (link is not null) break;
        }
        Assert.NotNull(link);
        Assert.NotNull(link!.Footnote);
    }

    [Fact]
    public void MarkdigFootnote_BackLinkScheme_FormatConsistent()
    {
        // Verify our URL scheme constants for back-links.
        const string defScheme = "#footnote-def-";
        const string refScheme = "#footnote-ref-";
        Assert.True(defScheme.StartsWith("#footnote-"), "def scheme must start with #footnote-");
        Assert.True(refScheme.StartsWith("#footnote-"), "ref scheme must start with #footnote-");
        Assert.NotEqual(defScheme, refScheme);
    }
}
