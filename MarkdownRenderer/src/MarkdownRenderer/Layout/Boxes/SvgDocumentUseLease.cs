using System;
using System.Threading;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// One counted use of an image box's opened SVG document. Disposing it
/// releases the use exactly once.
/// </summary>
internal sealed class SvgDocumentUseLease : IDisposable
{
    private SvgDocumentCustody? _owner;

    internal SvgDocumentUseLease(SvgDocumentCustody owner, SvgDocumentUseState state, bool candidate = false)
    {
        _owner = owner;
        State = state;
        IsCandidate = candidate;
    }

    internal readonly SvgDocumentUseState State;
    internal readonly bool IsCandidate;
    internal bool IsOwnedBy(SvgDocumentCustody owner) => ReferenceEquals(Volatile.Read(ref _owner), owner);

    public void Dispose() =>
        Interlocked.Exchange(ref _owner, null)?.CompleteUse(State, IsCandidate);
}
