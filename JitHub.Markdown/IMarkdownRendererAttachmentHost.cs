using System;

namespace JitHub.Markdown;

/// <summary>
/// What a <see cref="MarkdownRendererAttachment{TRenderer}"/> needs from the
/// host that shows its renderer.
/// </summary>
/// <typeparam name="TRenderer">The renderer control type.</typeparam>
public interface IMarkdownRendererAttachmentHost<TRenderer>
    where TRenderer : class
{
    /// <summary>Gets whether application shutdown has asked the host to stop creating renderers.</summary>
    bool IsShutdownRequested { get; }

    /// <summary>Gets whether the host is in the live visual tree.</summary>
    bool IsLoaded { get; }

    /// <summary>Queues work behind pending input and layout on the host's UI thread.</summary>
    /// <param name="callback">The work to run.</param>
    /// <returns><see langword="false"/> when the host has no dispatcher or it rejected the work.</returns>
    bool TryEnqueueLowPriority(Action callback);

    /// <summary>Gets whether the renderer is currently in the host's visual tree.</summary>
    /// <param name="renderer">The renderer.</param>
    bool IsRendererInTree(TRenderer renderer);

    /// <summary>Creates a renderer for the host's current viewport ownership.</summary>
    TRenderer CreateRenderer();

    /// <summary>
    /// Configures a renderer the slot has just taken ownership of: style,
    /// resources, initial settings, events and automation identity.
    /// </summary>
    /// <param name="renderer">The new renderer.</param>
    void ConfigureNewRenderer(TRenderer renderer);

    /// <summary>Prepares initial-layout gating for the renderer and adds it to the visual tree.</summary>
    /// <param name="renderer">The renderer.</param>
    void AddRendererToTree(TRenderer renderer);

    /// <summary>Applies the host's current settings and Markdown to its renderer.</summary>
    void ApplyRendererSettings();

    /// <summary>Shows that the host's renderer could not restart safely.</summary>
    void ShowRendererDisposalFailure();
}
