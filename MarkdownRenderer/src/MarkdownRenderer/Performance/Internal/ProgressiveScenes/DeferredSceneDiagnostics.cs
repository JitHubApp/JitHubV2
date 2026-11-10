using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Performance.Internal.ProgressiveScenes;

/// <summary>
/// Plan-wide diagnostics aggregated from each deferred scene's contribution and
/// kept in source order. The contribution map, sorted snapshot and publication
/// version are not thread-safe: the owning plan reads and writes them under its
/// gate, which also guards the active-work state they are checked against. The
/// publication semaphore serializes producers that compute a new snapshot off
/// that gate.
/// </summary>
internal sealed class DeferredSceneDiagnostics : IDisposable
{
    private static readonly IComparer<MarkdownDiagnostic> DiagnosticComparer =
        Comparer<MarkdownDiagnostic>.Create(CompareDiagnostics);

    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private ImmutableDictionary<string, ImmutableArray<MarkdownDiagnostic>> _contributions =
        ImmutableDictionary<string, ImmutableArray<MarkdownDiagnostic>>.Empty.WithComparers(StringComparer.Ordinal);
    private ImmutableList<MarkdownDiagnostic> _diagnostics = ImmutableList<MarkdownDiagnostic>.Empty;
    private long _publicationVersion;

    /// <summary>The current sorted snapshot. Call under the owning plan's gate.</summary>
    internal ImmutableList<MarkdownDiagnostic> Current => _diagnostics;

    internal Task WaitPublicationAsync(CancellationToken cancellationToken) =>
        _publicationGate.WaitAsync(cancellationToken);

    internal void ReleasePublication() => _publicationGate.Release();

    internal bool ContributionEquals(
        string marker,
        ImmutableArray<MarkdownDiagnostic> contribution)
    {
        if (!_contributions.TryGetValue(marker, out ImmutableArray<MarkdownDiagnostic> current))
            return contribution.IsEmpty;
        if (current.Length != contribution.Length)
            return false;
        for (int index = 0; index < current.Length; index++)
        {
            if (!EqualityComparer<MarkdownDiagnostic>.Default.Equals(current[index], contribution[index]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Captures the state a producer replaces off the gate and claims the next
    /// publication version, superseding any snapshot prepared earlier.
    /// </summary>
    internal DeferredSceneDiagnosticBasis BeginPublication(string marker)
    {
        if (!_contributions.TryGetValue(marker, out ImmutableArray<MarkdownDiagnostic> previousContribution))
            previousContribution = ImmutableArray<MarkdownDiagnostic>.Empty;
        return new DeferredSceneDiagnosticBasis(
            _contributions,
            _diagnostics,
            previousContribution,
            ++_publicationVersion);
    }

    internal bool IsCurrent(PreparedDiagnosticPublication publication) =>
        publication.Version == _publicationVersion;

    internal void Commit(PreparedDiagnosticPublication publication)
    {
        _contributions = publication.Contributions;
        _diagnostics = publication.Diagnostics;
    }

    /// <summary>Replaces one marker's contribution in place and supersedes prepared snapshots.</summary>
    internal void Publish(string marker, ImmutableArray<MarkdownDiagnostic> contribution)
    {
        if (ContributionEquals(marker, contribution))
            return;
        _contributions.TryGetValue(marker, out ImmutableArray<MarkdownDiagnostic> previous);
        _diagnostics = ReplaceContribution(_diagnostics,
            previous.IsDefault ? ImmutableArray<MarkdownDiagnostic>.Empty : previous, contribution);
        _contributions = contribution.IsEmpty
            ? _contributions.Remove(marker)
            : _contributions.SetItem(marker, contribution);
        _publicationVersion++;
    }

    /// <summary>Drops every contribution at plan retirement and supersedes prepared snapshots.</summary>
    internal void Reset()
    {
        _publicationVersion++;
        _contributions = ImmutableDictionary<string, ImmutableArray<MarkdownDiagnostic>>.Empty
            .WithComparers(StringComparer.Ordinal);
        _diagnostics = ImmutableList<MarkdownDiagnostic>.Empty;
    }

    internal static ImmutableList<MarkdownDiagnostic> ReplaceContribution(
        ImmutableList<MarkdownDiagnostic> snapshot,
        ImmutableArray<MarkdownDiagnostic> previous,
        ImmutableArray<MarkdownDiagnostic> replacement)
    {
        foreach (MarkdownDiagnostic diagnostic in previous)
        {
            int index = snapshot.BinarySearch(diagnostic, DiagnosticComparer);
            if (index >= 0)
                snapshot = snapshot.RemoveAt(index);
        }

        foreach (MarkdownDiagnostic diagnostic in replacement)
        {
            int index = snapshot.BinarySearch(diagnostic, DiagnosticComparer);
            if (index < 0)
                index = ~index;
            snapshot = snapshot.Insert(index, diagnostic);
        }

        return snapshot;
    }

    /// <summary>
    /// Disposes the publication semaphore. The plan calls this only after its
    /// tracked drain completes, when no producer can still wait for or release it.
    /// </summary>
    public void Dispose() => _publicationGate.Dispose();

    private static int CompareDiagnostics(MarkdownDiagnostic left, MarkdownDiagnostic right)
    {
        int comparison = left.SourceSpan.Start.CompareTo(right.SourceSpan.Start);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.Code, right.Code);
        if (comparison != 0)
            return comparison;
        comparison = left.SourceSpan.Length.CompareTo(right.SourceSpan.Length);
        if (comparison != 0)
            return comparison;
        comparison = left.Severity.CompareTo(right.Severity);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Message, right.Message);
    }
}
