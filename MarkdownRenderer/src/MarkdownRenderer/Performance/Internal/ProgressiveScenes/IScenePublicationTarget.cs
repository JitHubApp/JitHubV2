namespace MarkdownRenderer.Performance.Internal.ProgressiveScenes;

/// <summary>
/// Receives a queued publication on the UI thread. The dispatcher item holds
/// only the envelope; the envelope reaches its plan through this seam.
/// </summary>
internal interface IScenePublicationTarget
{
    void PublishOnUiThread(QueuedScenePublication publication);
}
