namespace MarkdownRenderer.Html;

/// <summary>Describes how external resources are routed by the safe-HTML pack.</summary>
public enum SafeHtmlExternalResourceRouting
{
    /// <summary>The resource kind is disabled.</summary>
    Disabled,
    /// <summary>The resource is passed to host-owned policy and resolution services.</summary>
    HostMediated,
}

/// <summary>
/// Security properties hosts can inspect without depending on a parser implementation.
/// </summary>
public sealed class SafeHtmlCapabilityDescriptor
{
    internal SafeHtmlCapabilityDescriptor()
    {
    }

    /// <summary>Gets the owning NuGet package identifier.</summary>
    public string PackageId => SafeHtmlFeature.PackageId;

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

/// <summary>Marker implemented by objects that register the safe-HTML feature with a host.</summary>
public interface ISafeHtmlCapabilityMarker
{
    /// <summary>Gets the immutable security capability descriptor.</summary>
    SafeHtmlCapabilityDescriptor SafeHtmlCapabilities { get; }

    /// <summary>Gets the immutable host policy.</summary>
    SafeHtmlOptions SafeHtmlOptions { get; }
}

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

/// <summary>Stable metadata for the native safe-HTML feature pack.</summary>
public static class SafeHtmlFeature
{
    /// <summary>The owning NuGet package identifier.</summary>
    public const string PackageId = "MarkdownRenderer.Html";

    /// <summary>The stable capability contract version.</summary>
    public const int ContractVersion = 1;

    /// <summary>Gets the immutable security capability descriptor.</summary>
    public static SafeHtmlCapabilityDescriptor Capabilities { get; } = new();
}
