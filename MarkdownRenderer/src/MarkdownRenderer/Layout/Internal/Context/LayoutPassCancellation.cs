using System;
using System.Threading;

namespace MarkdownRenderer.Layout.Internal.Context;

/// <summary>
/// Supplies the cancellation token of the synchronous measure pass that is
/// using one layout context. Lazy viewport realization uses a different token
/// from the original snapshot build, so boxes must observe the pass token at
/// their existing nested row/run/child checkpoints.
/// </summary>
/// <remarks>
/// Each <see cref="MarkdownLayoutContext"/> owns one instance, and a placement
/// clone gets a fresh context and so a fresh instance. Only the pass that owns
/// the context pushes a token: the builder before the snapshot is published,
/// and afterwards the holder of the snapshot layout gate. Push inside that gate
/// so a waiting pass cannot replace the token of the pass that holds it.
/// </remarks>
internal sealed class LayoutPassCancellation
{
    private CancellationToken _currentToken;
    private bool _hasCurrentToken;

    internal CancellationToken GetEffectiveToken(CancellationToken fallback)
        => _hasCurrentToken ? _currentToken : fallback;

    internal Scope Push(CancellationToken cancellationToken)
    {
        var scope = new Scope(this, _hasCurrentToken, _currentToken);
        _currentToken = cancellationToken;
        _hasCurrentToken = true;
        return scope;
    }

    private void Restore(bool hadPreviousToken, CancellationToken previousToken)
    {
        _currentToken = previousToken;
        _hasCurrentToken = hadPreviousToken;
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly LayoutPassCancellation? _owner;
        private readonly bool _hadPreviousToken;
        private readonly CancellationToken _previousToken;

        internal Scope(LayoutPassCancellation owner, bool hadPreviousToken, CancellationToken previousToken)
        {
            _owner = owner;
            _hadPreviousToken = hadPreviousToken;
            _previousToken = previousToken;
        }

        public void Dispose() => _owner?.Restore(_hadPreviousToken, _previousToken);
    }
}
