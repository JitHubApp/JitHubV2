namespace MarkdownRenderer.Performance.Internal.ProgressiveScenes;

/// <summary>Lifecycle of one queued deferred-scene publication.</summary>
internal enum ScenePublicationState : byte
{
    Created,
    Queued,
    Executing,
    Canceled,
    Finished,
}
