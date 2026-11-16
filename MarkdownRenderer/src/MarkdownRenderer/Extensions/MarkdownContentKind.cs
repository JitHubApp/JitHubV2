namespace MarkdownRenderer.Extensions;

/// <summary>Identifies a parser-independent declarative content primitive.</summary>
public enum MarkdownContentKind
{
    /// <summary>Semantic text.</summary>
    Text,
    /// <summary>A generic semantic container.</summary>
    Container,
    /// <summary>A hyperlink container.</summary>
    Link,
    /// <summary>A list container.</summary>
    List,
    /// <summary>A list item container.</summary>
    ListItem,
    /// <summary>A table container.</summary>
    Table,
    /// <summary>A table row container.</summary>
    TableRow,
    /// <summary>A table cell container.</summary>
    TableCell,
    /// <summary>A code block.</summary>
    CodeBlock,
    /// <summary>An image resolved through the host's image policy.</summary>
    Image,
    /// <summary>An immutable painter-neutral vector scene.</summary>
    VectorScene,
    /// <summary>A hosted element resolved through a registered factory key.</summary>
    HostedElement,
    /// <summary>A named extension primitive interpreted by a feature pack.</summary>
    Custom
}
