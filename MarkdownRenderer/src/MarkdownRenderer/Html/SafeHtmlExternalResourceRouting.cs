namespace MarkdownRenderer.Html;

/// <summary>Describes how the safe-HTML feature routes external resources.</summary>
public enum SafeHtmlExternalResourceRouting
{
    /// <summary>The resource kind is disabled.</summary>
    Disabled,
    /// <summary>The resource is passed to host-owned policy and resolution services.</summary>
    HostMediated,
}
