namespace MarkdownRenderer.Html;

/// <summary>Immutable default marker suitable for declarative extension registration.</summary>
public sealed class SafeHtmlCapabilityMarker : ISafeHtmlCapabilityMarker
{
    /// <summary>Creates a marker for the supplied policy, or the audited defaults.</summary>
    /// <param name="options">Optional safe-HTML policy.</param>
    public SafeHtmlCapabilityMarker(SafeHtmlOptions? options = null)
    {
        SafeHtmlOptions = options ?? SafeHtmlOptions.Default;
    }

    /// <inheritdoc />
    public SafeHtmlCapabilityDescriptor SafeHtmlCapabilities => SafeHtmlFeature.Capabilities;

    /// <inheritdoc />
    public SafeHtmlOptions SafeHtmlOptions { get; }
}
