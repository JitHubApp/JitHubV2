namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// What a queued gesture that waited for geometry does next in a
/// <see cref="PointerInputRouter"/> drain pass.
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
