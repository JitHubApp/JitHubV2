using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Tests.Engine;

/// <summary>
/// An extension with a given id whose registrations are made by the
/// configure callback the test passes in.
/// </summary>
internal sealed class ConfigurableExtension(
    string id,
    Action<MarkdownExtensionBuilder> configure) : IMarkdownExtension
{
    public string Id => id;

    public void Configure(MarkdownExtensionBuilder builder) => configure(builder);
}
