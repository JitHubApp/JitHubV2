using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Embeds;

/// <summary>Where <see cref="InlineContainerPlans"/> sends a paragraph's image and hosted element plans.</summary>
internal interface IInlinePlanCollector
{
    /// <summary>Subscribes to <paramref name="image"/> and adds its plan.</summary>
    void AddImagePlan(ImageBox image);

    /// <summary>Collects the plans of the paragraph's inline hosted elements.</summary>
    void CollectInlineEmbeds(LayoutSnapshot snapshot, BlockBox topLevelRoot, InlineContainerBox box);
}
