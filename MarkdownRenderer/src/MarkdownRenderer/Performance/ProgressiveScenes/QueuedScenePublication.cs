using System;
using System.Threading;

namespace MarkdownRenderer.Performance.ProgressiveScenes;

/// <summary>A prepared deferred-scene result waiting for its UI-thread publication.</summary>
internal sealed class QueuedScenePublication(
    IScenePublicationTarget owner,
    string marker,
    ActiveSceneWork activeWork,
    CancellationToken generationToken,
    MarkdownDeferredSceneResult result,
    MarkdownCompletedSceneDescriptor descriptor,
    PreparedDiagnosticPublication? diagnosticPublication,
    Action onPublished,
    Action? onRetryVisibleBand)
{
    internal IScenePublicationTarget Owner { get; } = owner;
    internal string Marker { get; } = marker;
    internal ActiveSceneWork ActiveWork { get; } = activeWork;
    internal CancellationToken GenerationToken { get; } = generationToken;
    internal MarkdownDeferredSceneResult Result { get; } = result;
    internal MarkdownCompletedSceneDescriptor Descriptor { get; } = descriptor;
    internal PreparedDiagnosticPublication? DiagnosticPublication { get; } = diagnosticPublication;
    internal Action OnPublished { get; } = onPublished;
    internal Action? OnRetryVisibleBand { get; } = onRetryVisibleBand;
}
