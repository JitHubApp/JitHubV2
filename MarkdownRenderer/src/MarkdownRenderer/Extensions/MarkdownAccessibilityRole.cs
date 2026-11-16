namespace MarkdownRenderer.Extensions;

/// <summary>Accessibility semantics carried by declarative extension content.</summary>
public enum MarkdownAccessibilityRole
{
    /// <summary>No additional semantic role.</summary>
    None,
    /// <summary>Text content.</summary>
    Text,
    /// <summary>A semantic group.</summary>
    Group,
    /// <summary>A paragraph.</summary>
    Paragraph,
    /// <summary>A heading.</summary>
    Heading,
    /// <summary>An invokable hyperlink.</summary>
    Link,
    /// <summary>An image.</summary>
    Image,
    /// <summary>A code region.</summary>
    Code,
    /// <summary>A list.</summary>
    List,
    /// <summary>A list item.</summary>
    ListItem,
    /// <summary>A table.</summary>
    Table,
    /// <summary>A table row.</summary>
    Row,
    /// <summary>A table cell.</summary>
    Cell,
    /// <summary>A mathematical expression.</summary>
    Math,
    /// <summary>A diagram.</summary>
    Diagram,
    /// <summary>A live status message.</summary>
    Status
}
