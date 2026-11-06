namespace JitHub.Markdown;

/// <summary>
/// A Markdown host that borrows a renderer and must release it before the
/// application retires the shared Markdown providers.
/// </summary>
public interface IMarkdownRendererShutdownParticipant
{
    /// <summary>
    /// Stops renderer creation and retires the host's renderer now, even when
    /// WinUI has not yet delivered the host's Unloaded event.
    /// </summary>
    void PrepareForApplicationShutdown();
}
