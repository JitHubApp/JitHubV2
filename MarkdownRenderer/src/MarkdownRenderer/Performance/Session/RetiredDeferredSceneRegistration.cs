using System;

namespace MarkdownRenderer.Performance;

/// <summary>
/// The sorted marker snapshot of a plan released at session shutdown, so a
/// control can still recognize its pending placeholders after disposal.
/// </summary>
internal sealed class RetiredDeferredSceneRegistration(string[] markers)
{
    internal bool HasRegisteredWork => markers.Length != 0;

    internal bool Contains(string marker) =>
        !string.IsNullOrEmpty(marker) &&
        Array.BinarySearch(markers, marker, StringComparer.Ordinal) >= 0;
}
