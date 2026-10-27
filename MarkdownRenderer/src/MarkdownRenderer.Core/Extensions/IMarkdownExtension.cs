namespace MarkdownRenderer.Extensions;

/// <summary>
/// Configures parser-independent markdown features and declarative renderers.
/// </summary>
/// <remarks>
/// Implementations are invoked while a <c>MarkdownEngine</c> is being built.
/// They must not retain the supplied builder or assume that configuration runs
/// on the UI thread. Registered renderer callbacks may later run concurrently
/// for independent documents, so their captured state must be thread-safe.
/// </remarks>
public interface IMarkdownExtension
{
    /// <summary>
    /// Gets a stable, package-qualified identifier such as
    /// <c>MarkdownRenderer.Gfm</c>.
    /// </summary>
    string Id { get; }

    /// <summary>Adds features and renderers to the engine configuration.</summary>
    void Configure(MarkdownExtensionBuilder builder);
}
