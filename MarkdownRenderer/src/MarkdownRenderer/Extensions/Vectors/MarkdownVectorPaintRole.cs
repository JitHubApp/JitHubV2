namespace MarkdownRenderer.Extensions.Vectors;

/// <summary>
/// Controls whether a vector paint keeps its authored color or resolves from
/// the host's semantic palette at draw time.
/// </summary>
public enum MarkdownVectorPaintRole
{
    /// <summary>
    /// Keeps a non-null authored color outside Windows High Contrast. A null
    /// color retains the legacy semantic-foreground behavior.
    /// </summary>
    Authored,
    /// <summary>Uses the host's primary text/stroke color.</summary>
    Foreground,
    /// <summary>Uses the host's contrasting diagram or control surface color.</summary>
    Surface,
    /// <summary>Uses the host's hyperlink color.</summary>
    Link,
}
