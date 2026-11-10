using System;
using System.Collections.Generic;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.Placement;

namespace MarkdownRenderer.Controls.Images;

/// <summary>
/// Completed image results kept across rebuilds of the same document. The
/// ledger accumulates across consecutive rebuilds (a lazily rebuilt
/// document lists only its measured images) and is discarded whenever the
/// source text or image configuration changes. UI thread only; a rebuild
/// receives an immutable copy.
/// </summary>
internal sealed class CompletedImageCarryLedger
{
    private const int MaximumEntries = 4096;

    private readonly Dictionary<string, CompletedImageCarry> _entries = new(StringComparer.Ordinal);
    private MarkdownImageCarryIdentity? _identity;

    /// <summary>
    /// Records the completed images among <paramref name="plans"/> so the
    /// next rebuild of the same document keeps their result, and returns a
    /// copy of the ledger for that rebuild, or null when it is empty.
    /// Failures are included only when <paramref name="carryFailures"/>.
    /// </summary>
    internal IReadOnlyDictionary<string, CompletedImageCarry>? Capture(
        MarkdownImageCarryIdentity identity,
        bool carryFailures,
        List<ImageBox> plans)
    {
        bool identityReset = _identity is not { } previous || !previous.Matches(identity);
        if (identityReset)
            _entries.Clear();
        _identity = identity;

        foreach (ImageBox image in plans)
        {
            if (image.Context.ImageCarryIdentity is not { } builtWith || !builtWith.Matches(identity))
                continue;
            if (!image.TryCaptureCompletedCarry(out CompletedImageCarry? carry) || carry is null)
                continue;
            if (_entries.Count >= MaximumEntries &&
                !_entries.ContainsKey(carry.Url))
            {
                break;
            }
            _entries[carry.Url] = carry;
        }

        var snapshot = new Dictionary<string, CompletedImageCarry>(StringComparer.Ordinal);
        foreach ((string url, CompletedImageCarry carry) in _entries)
        {
            if (carryFailures || !carry.Failed)
                snapshot[url] = carry;
        }

        return snapshot.Count == 0 ? null : snapshot;
    }
}
