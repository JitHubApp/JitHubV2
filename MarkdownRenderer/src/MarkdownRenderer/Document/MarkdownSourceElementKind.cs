namespace MarkdownRenderer.Document;

/// <summary>Classifies an immutable entry in a document's semantic source map.</summary>
public enum MarkdownSourceElementKind
{
    /// <summary>A heading block.</summary>
    Heading,

    /// <summary>A non-image link.</summary>
    Link,

    /// <summary>A fenced or indented code block.</summary>
    CodeBlock,

    /// <summary>An image.</summary>
    Image,

    /// <summary>A footnote definition.</summary>
    Footnote,

    /// <summary>A definition-list item.</summary>
    DefinitionItem,

    /// <summary>An abbreviation occurrence.</summary>
    Abbreviation,

    /// <summary>A named fragment target.</summary>
    Fragment,
}
