namespace MarkdownRenderer.Layout;

/// <summary>The kind of keyboard-focusable element represented by <see cref="FocusableItem"/>.</summary>
public enum FocusableItemKind
{
    Link,
    InlineEmbed,
    BlockEmbed,
}

/// <summary>Represents a keyboard-focusable element in the document.</summary>
public readonly struct FocusableItem
{
    public FocusableItem(int blockIndex, int inlineIndex, bool isLink) =>
        (BlockIndex, InlineIndex, Kind) = (blockIndex, inlineIndex, isLink ? FocusableItemKind.Link : FocusableItemKind.InlineEmbed);

    public FocusableItem(int blockIndex, int inlineIndex, FocusableItemKind kind) =>
        (BlockIndex, InlineIndex, Kind) = (blockIndex, inlineIndex, kind);

    public int BlockIndex { get; }
    public int InlineIndex { get; }
    public FocusableItemKind Kind { get; }
    public bool IsLink => Kind == FocusableItemKind.Link;
    public bool IsInlineEmbed => Kind == FocusableItemKind.InlineEmbed;
    public bool IsBlockEmbed => Kind == FocusableItemKind.BlockEmbed;
}
