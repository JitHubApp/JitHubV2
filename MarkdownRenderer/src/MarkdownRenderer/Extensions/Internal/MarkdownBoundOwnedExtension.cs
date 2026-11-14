using System;

namespace MarkdownRenderer.Extensions.Internal;

/// <summary>
/// Carries the factory-validated identifier with its engine-local extension so
/// binding never needs to execute the extension's <see cref="IMarkdownExtension.Id"/>
/// getter a second time.
/// </summary>
internal readonly struct MarkdownBoundOwnedExtension(
    IMarkdownExtension Extension,
    IDisposable Resource,
    string ExtensionId)
{
    public IMarkdownExtension Extension { get; } = Extension;
    public IDisposable Resource { get; } = Resource;
    public string ExtensionId { get; } = ExtensionId;
}
