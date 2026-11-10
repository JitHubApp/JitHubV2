namespace MarkdownRenderer.Layout.Internal.Focus;

/// <summary>The kind of keyboard-focusable element represented by <see cref="FocusableItem"/>.</summary>
internal enum FocusableItemKind
{
    Link,
    InlineEmbed,
    BlockEmbed,
    DeclarativeHostedElement,
    CodeBlockCopy,
    VectorSemantic,
    HorizontalOverflow,
}
