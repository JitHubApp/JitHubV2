namespace MarkdownRenderer.Controls.Internal.Input.Deferred;

/// <summary>
/// What a queued gesture that waited for geometry does next in a
/// <see cref="DeferredInputReplay"/> drain pass.
/// </summary>
internal enum DeferredGeometryWaitStep
{
    /// <summary>Dequeue and dispatch the gesture now.</summary>
    Dispatch,

    /// <summary>The gesture was dropped; go on with the next queued input.</summary>
    Skip,

    /// <summary>End the drain pass.</summary>
    Stop,
}
