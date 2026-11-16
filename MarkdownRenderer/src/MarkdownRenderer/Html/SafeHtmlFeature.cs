namespace MarkdownRenderer.Html;

/// <summary>Stable metadata for the native safe-HTML feature.</summary>
public static class SafeHtmlFeature
{
    /// <summary>The stable capability contract version.</summary>
    public const int ContractVersion = 1;

    /// <summary>Gets the immutable security capability descriptor.</summary>
    public static SafeHtmlCapabilityDescriptor Capabilities { get; } = new();
}
