namespace MarkdownRenderer.Html;

/// <summary>
/// Security properties hosts can inspect without depending on a parser implementation.
/// </summary>
public sealed class SafeHtmlCapabilityDescriptor
{
    internal SafeHtmlCapabilityDescriptor()
    {
    }

    /// <summary>Gets the stable capability contract version.</summary>
    public int ContractVersion => SafeHtmlFeature.ContractVersion;

    /// <summary>Gets whether parsing and painting are native rather than browser-hosted.</summary>
    public bool IsNativeSubset => true;

    /// <summary>Gets whether the feature can execute script.</summary>
    public bool ExecutesScript => false;

    /// <summary>Gets whether the feature applies arbitrary CSS layout.</summary>
    public bool AppliesCssLayout => false;

    /// <summary>Gets whether a browser DOM is exposed.</summary>
    public bool ExposesBrowserDom => false;

    /// <summary>Gets whether the parser or painter accesses the filesystem.</summary>
    public bool HasFilesystemAccess => false;

    /// <summary>Gets whether the parser or painter performs direct network access.</summary>
    public bool HasDirectNetworkAccess => false;

    /// <summary>Gets how links are routed.</summary>
    public SafeHtmlExternalResourceRouting LinkRouting => SafeHtmlExternalResourceRouting.HostMediated;

    /// <summary>Gets how images are routed.</summary>
    public SafeHtmlExternalResourceRouting ImageRouting => SafeHtmlExternalResourceRouting.HostMediated;

    /// <summary>Gets whether public source ranges are half-open UTF-16 offsets.</summary>
    public bool UsesHalfOpenUtf16SourceRanges => true;
}
