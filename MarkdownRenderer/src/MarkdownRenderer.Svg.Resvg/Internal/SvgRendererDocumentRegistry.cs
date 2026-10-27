using System.Runtime.CompilerServices;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Tracks live document lifetimes without making the registry an owner of
/// healthy documents or their source graphs. The shutdown snapshot is the
/// only strong ownership this registry creates.
/// </summary>
internal sealed class SvgRendererDocumentRegistry
{
    private readonly object _gate = new();
    private ConditionalWeakTable<SvgDocumentLifetime, Marker>? _documents = new();

    /// <summary>
    /// Registers a lifetime until it becomes unreachable or the registry is
    /// closed. Re-registering the same lifetime is harmless. Already-disposed
    /// lifetimes are accepted as well; their idempotent disposal and retirement
    /// barrier make them safe entries in the shutdown snapshot.
    /// </summary>
    internal bool TryRegister(SvgDocumentLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);

        lock (_gate)
        {
            ConditionalWeakTable<SvgDocumentLifetime, Marker>? documents = _documents;
            if (documents is null)
                return false;

            if (!documents.TryGetValue(lifetime, out _))
                documents.Add(lifetime, Marker.Instance);

            return true;
        }
    }

    /// <summary>
    /// Atomically closes registration and returns the currently live lifetimes
    /// as a strong shutdown snapshot. This method never cancels or disposes a
    /// lifetime; callers perform those operations after this lock is released.
    /// </summary>
    internal IReadOnlyList<SvgDocumentLifetime> Close()
    {
        lock (_gate)
        {
            ConditionalWeakTable<SvgDocumentLifetime, Marker>? documents = _documents;
            if (documents is null)
                return Array.Empty<SvgDocumentLifetime>();

            var snapshot = new List<SvgDocumentLifetime>();
            foreach (KeyValuePair<SvgDocumentLifetime, Marker> entry in documents)
                snapshot.Add(entry.Key);

            // Publish the terminal state only after the snapshot is complete.
            // If snapshot allocation fails, callers may safely retry Close.
            _documents = null;
            return snapshot;
        }
    }

    private sealed class Marker
    {
        internal static readonly Marker Instance = new();

        private Marker()
        {
        }
    }
}
