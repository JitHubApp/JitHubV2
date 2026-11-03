using System;

namespace JitHub.Services.Markdown;

/// <summary>
/// What a <see cref="MarkdownRendererSlot{TRenderer}"/> needs from the host whose renderer it owns.
/// </summary>
/// <typeparam name="TRenderer">The renderer control type.</typeparam>
internal interface IMarkdownRendererSlotHost<in TRenderer>
    where TRenderer : class
{
    /// <summary>Gets the host's UI dispatcher, or <see langword="null"/> when it has none.</summary>
    IMarkdownUiDispatcher? Dispatcher { get; }

    /// <summary>
    /// Detaches the renderer from the host: event handlers, pending layout
    /// callbacks and the visual tree. Called immediately before disposal.
    /// </summary>
    /// <param name="renderer">The renderer being retired.</param>
    void DetachRenderer(TRenderer renderer);

    /// <summary>
    /// Called on the UI thread once a retired renderer's resources have drained
    /// and its tracking is released, so the host may create a replacement.
    /// </summary>
    void OnRendererReleased();

    /// <summary>
    /// Called when a renderer could not be torn down or its retired resources
    /// failed to drain. The slot has already latched the faulted state.
    /// </summary>
    /// <param name="exception">The failure to report.</param>
    void OnRendererTeardownFaulted(Exception exception);
}
