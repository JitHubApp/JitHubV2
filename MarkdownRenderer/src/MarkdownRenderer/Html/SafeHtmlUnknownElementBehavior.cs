namespace MarkdownRenderer.Html;

/// <summary>Determines how elements outside the native safe subset are presented.</summary>
public enum SafeHtmlUnknownElementBehavior
{
    /// <summary>Shows the exact tag markup as inert text.</summary>
    RenderLiteral,
    /// <summary>Omits unknown tags while preserving their non-suppressed text content.</summary>
    KeepTextContent,
}
