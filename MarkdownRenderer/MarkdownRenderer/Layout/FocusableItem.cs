namespace MarkdownRenderer.Layout;

/// <summary>Represents a keyboard-focusable element (link or inline embed) in the document.</summary>
public readonly struct FocusableItem
{
    public FocusableItem(int blockIndex, int inlineIndex, bool isLink) =>
        (BlockIndex, InlineIndex, IsLink) = (blockIndex, inlineIndex, isLink);
    public int BlockIndex { get; }
    public int InlineIndex { get; }
    /// <summary>True for <see cref="Boxes.LinkRun"/>, false for <see cref="Boxes.InlineEmbedRun"/>.</summary>
    public bool IsLink { get; }
}
