using System;

namespace MarkdownRenderer.Extensions;

/// <summary>
/// Couples one engine-local extension with the disposable resource captured by
/// its renderer callbacks.
/// </summary>
/// <remarks>
/// Instances are created by a factory registered through
/// <see cref="MarkdownEngineBuilder.UseOwnedExtensionFactory"/>. After the factory
/// returns successfully, the receiving engine owns <see cref="Resource"/>; callers
/// must not dispose or reuse that resource independently. The extension object is
/// configuration data and is not disposed separately.
/// </remarks>
public sealed class MarkdownOwnedExtension
{
    /// <summary>Creates an extension/resource pair whose resource transfers to one engine.</summary>
    public MarkdownOwnedExtension(IMarkdownExtension extension, IDisposable resource)
    {
        Extension = extension ?? throw new ArgumentNullException(nameof(extension));
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
    }

    /// <summary>Gets the extension configured for one engine.</summary>
    public IMarkdownExtension Extension { get; }

    /// <summary>Gets the resource owned and eventually disposed by that engine.</summary>
    public IDisposable Resource { get; }
}
