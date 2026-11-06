namespace MarkdownRenderer.Html;

/// <summary>Marker implemented by objects that register the safe-HTML feature with a host.</summary>
public interface ISafeHtmlCapabilityMarker
{
    /// <summary>Gets the immutable security capability descriptor.</summary>
    SafeHtmlCapabilityDescriptor SafeHtmlCapabilities { get; }

    /// <summary>Gets the immutable host policy.</summary>
    SafeHtmlOptions SafeHtmlOptions { get; }
}
