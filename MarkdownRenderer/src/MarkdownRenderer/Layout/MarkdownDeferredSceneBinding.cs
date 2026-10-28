namespace MarkdownRenderer.Layout;

/// <summary>
/// Identifies one materialized scene slot without retaining its content or
/// command graph. This is not the readable placeholder's scheduling marker.
/// </summary>
internal readonly struct MarkdownDeferredSceneBinding(string marker, int sceneOrdinal, object? projectionIdentity = null)
{
    internal readonly string Marker = marker;
    internal readonly int SceneOrdinal = sceneOrdinal;
    internal readonly object? ProjectionIdentity = projectionIdentity;
}
