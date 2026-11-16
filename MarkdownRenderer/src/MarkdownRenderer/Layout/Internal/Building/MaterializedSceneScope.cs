using MarkdownRenderer.Layout.Internal.Vectors;

namespace MarkdownRenderer.Layout.Internal.Building;

/// <summary>
/// Scene-free identity of the worker result currently being materialized.
/// A worker result may contain nested containers or several scene owners;
/// its identity is scoped through construction, never through its fragment.
/// </summary>
internal sealed class MaterializedSceneScope
{
    internal string? Marker { get; set; }

    internal int Ordinal { get; set; }

    internal object? ProjectionIdentity { get; set; }

    internal MarkdownDeferredSceneBinding? NextBinding() =>
        Marker is { } marker
            ? new MarkdownDeferredSceneBinding(marker, Ordinal++, ProjectionIdentity)
            : null;
}
