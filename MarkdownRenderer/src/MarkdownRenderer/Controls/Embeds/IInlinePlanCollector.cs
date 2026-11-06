using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Controls.Embeds;

/// <summary>Where <see cref="InlineContainerPlans"/> sends a paragraph's image and hosted element plans.</summary>
internal interface IInlinePlanCollector
{
    /// <summary>Subscribes to <paramref name="image"/> and adds its plan.</summary>
    void AddImagePlan(ImageBox image);

    /// <summary>Collects the plans of the paragraph's inline hosted elements.</summary>
    void CollectInlineEmbeds(LayoutSnapshot snapshot, BlockBox topLevelRoot, InlineContainerBox box);
}
