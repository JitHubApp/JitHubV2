using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Registers, adopts and discards boxes in the context's construction scope so
/// a failed build can retire every box it created and nothing it published.
/// </summary>
internal sealed class LayoutConstructionTracker
{
    private readonly MarkdownLayoutContext _context;

    internal LayoutConstructionTracker(
        MarkdownLayoutContext context)
    {
        _context = context;
    }

    internal MarkdownLayoutConstructionScope BeginBlockBuildOwnership() =>
        _context.Construction.Begin();

    internal void EndBlockBuildOwnership(MarkdownLayoutConstructionScope ownership) =>
        ownership.Dispose();

    internal T TrackBlock<T>(T block) where T : BlockBox
    {
        _context.RegisterConstructionBlock(block);
        return block;
    }

    internal void AdoptBlock(BlockBox child) => _context.AdoptConstructionBlock(child);

    internal void AddStackChild(StackBox parent, BlockBox child)
    {
        parent.Add(child);
        AdoptBlock(child);
    }

    internal InlineImageRun TrackInlineImageRun(InlineImageRun run)
    {
        TrackBlock(run.Image);
        return run;
    }

    internal InlineRun? TrackInlineImageRun(InlineRun? run)
    {
        if (run is InlineImageRun image)
            TrackBlock(image.Image);
        return run;
    }

    internal void AddInlineRun(InlineContainerBox parent, InlineRun run)
    {
        parent.Add(run);
        if (run is InlineImageRun image)
            AdoptBlock(image.Image);
        else if (run is InlineVectorSceneRun vector)
            _context.Construction.AdoptResource(vector);
    }

    internal void AddCodeBlockChunk(CodeBlockBox parent, InlineContainerBox child)
    {
        parent.AddChunk(child);
        AdoptBlock(child);
    }

    internal void DiscardUnpublishedBlock(BlockBox block)
    {
        _context.DiscardConstructionBlock(block);
    }

    internal void AddHtmlBudgetNotice(InlineContainerBox box) => box.Add(new TextRun(
        _context.ResolveString(MarkdownStringKeys.HtmlBudgetExceeded, MarkdownLocalizedStrings.HtmlBudgetExceeded))
    {
        SourceSpan = SourceSpan.Empty,
    });
}
