using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Embeds;

/// <summary>
/// Collects the plans of a paragraph's inline images and hosted elements.
/// Every inline image is subscribed before DirectWrite is asked where it is,
/// so an image whose character region is not yet available still loads and
/// its completion still reflows the paragraph.
/// </summary>
internal static class InlineContainerPlans
{
    internal static void Collect(
        IInlinePlanCollector collector,
        LayoutSnapshot snapshot,
        BlockBox topLevelRoot,
        InlineContainerBox icb)
    {
        // Most paragraphs contain only text. Avoid walking their runs
        // and querying DirectWrite geometry when no overlay/image
        // plan can possibly be produced.
        if (!icb.HasInlineImages && !icb.HasInlineEmbeds)
            return;

        // Register completion before asking DirectWrite for rectangles. During the
        // first commit an inline image can be measurable but not yet have a stable
        // character region; paint will start it and completion must still reflow.
        if (icb.HasInlineImages)
        {
            foreach (var (imageRun, _) in icb.InlineImageRuns)
            {
                // Image registration must not depend on DirectWrite
                // returning a character rectangle for the object
                // replacement slot. In particular, a linked image on
                // an otherwise empty line can temporarily have no
                // region during the first layout pass. Keeping it out
                // of the image plans in that state leaves it permanently
                // "Loading" because no later viewport pass can start
                // it. A missing first-pass rectangle has the default
                // origin, which deliberately starts the bounded load;
                // EnumerateInlineImageRects synchronizes the real
                // bounds as soon as DirectWrite publishes them.
                collector.AddImagePlan(imageRun.Image);
            }
        }
        if (icb.HasInlineEmbeds)
            collector.CollectInlineEmbeds(snapshot, topLevelRoot, icb);
        // Force enumeration so each ImageBox receives its document
        // bounds. Images were already added above, independently of
        // whether this geometry query succeeds.
        if (icb.HasInlineImages)
        {
            foreach (var _ in icb.EnumerateInlineImageRects()) { }
        }
    }
}
