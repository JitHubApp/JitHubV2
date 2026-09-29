namespace MarkdownRenderer;

/// <summary>
/// Opaque bridge that lets the UI package freeze presentation registrations
/// onto an engine without making WinUI/layout types part of the Core API.
/// </summary>
internal interface IMarkdownPresentationConfiguration
{
    bool IsFrozen { get; }

    IMarkdownPresentationConfiguration Freeze();

    IMarkdownPresentationConfiguration CreateMutableCopy();
}
