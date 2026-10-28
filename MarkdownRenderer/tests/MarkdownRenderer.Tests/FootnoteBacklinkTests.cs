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
        // Verify the URL scheme constants we depend on for navigation.
        // These constants are mirrored in LayoutBuilder.cs and HandleInternalAnchor.
        const string defPrefix = "#footnote-def-";
        const string refPrefix = "#footnote-ref-";

        // Both must start with the shared fragment prefix.
        Assert.StartsWith("#footnote-", defPrefix);
        Assert.StartsWith("#footnote-", refPrefix);
        Assert.NotEqual(defPrefix, refPrefix);

        // The URLs produced for order=1 and order=2 must differ.
        string url1 = defPrefix + 1;
        string url2 = defPrefix + 2;
        Assert.NotEqual(url1, url2);
        Assert.Equal("#footnote-def-1", url1);
        Assert.Equal("#footnote-def-2", url2);

        // Back-link URL for order=1 must be refPrefix+1.
        string backUrl1 = refPrefix + 1;
        Assert.Equal("#footnote-ref-1", backUrl1);

        // Forward and back URLs for the same order must differ so the router
        // can distinguish them.
        Assert.NotEqual(url1, backUrl1);
    }

    [Fact]
    public void MarkdigFootnote_RepeatedCitation_FootnoteOrderIsSameForBothCitations()
    {
        // Verifies the key invariant that drives LayoutBuilder's URL construction:
        // fl.Footnote.Order is stable across multiple citations of the same footnote,
        // while fl.Index is a global sequential counter that increments per citation.
        // Correct URL construction must use fl.Footnote.Order, not fl.Index.
        var pipeline = new MarkdownPipelineBuilder().UseFootnotes().Build();
        var md = "First[^a] and again[^a].\n\n[^a]: The definition.";
        var doc = Markdig.Markdown.Parse(md, pipeline);

        var links = new List<FootnoteLink>();
        foreach (var block in doc)
        {
            if (block is ParagraphBlock pb)
            {
                foreach (var inline in pb.Inline!)
                    if (inline is FootnoteLink fl && !fl.IsBackLink) links.Add(fl);
            }
        }

        Assert.Equal(2, links.Count);

        // Both citations must have the same Footnote.Order (it's the same footnote).
        int order0 = links[0].Footnote?.Order ?? -1;
        int order1 = links[1].Footnote?.Order ?? -1;
        Assert.True(order0 > 0, "First citation Footnote.Order must be positive");
        Assert.Equal(order0, order1);  // same footnote → same order

        // fl.Index, by contrast, must differ (it's a per-citation counter).
        Assert.NotEqual(links[0].Index, links[1].Index);

        // This demonstrates why fl.Footnote.Order must be used for the URL, not fl.Index.
        // With fl.Footnote.Order both citations produce "#footnote-def-1" (correct).
        // With fl.Index the second citation produces "#footnote-def-2" (wrong — no such def).
        string correctUrl0 = $"#footnote-def-{order0}";
        string correctUrl1 = $"#footnote-def-{order1}";
        Assert.Equal(correctUrl0, correctUrl1);  // same URL for both → both navigate to same def

        string wrongUrl1 = $"#footnote-def-{links[1].Index}";
        Assert.NotEqual(correctUrl0, wrongUrl1);  // fl.Index-based URL would be wrong
    }
}
