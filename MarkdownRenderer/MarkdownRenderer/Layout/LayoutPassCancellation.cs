using System;
using System.Threading;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Supplies the cancellation token for the synchronous measure pass currently
/// executing on this thread. Lazy viewport realization uses a different token
/// from the original snapshot build, so boxes must observe the pass token at
/// their existing nested row/run/child checkpoints.
/// </summary>
internal static class LayoutPassCancellation
{
    [ThreadStatic]
    private static CancellationToken _currentToken;

    [ThreadStatic]
    private static bool _hasCurrentToken;

    internal static CancellationToken GetEffectiveToken(CancellationToken fallback)
        => _hasCurrentToken ? _currentToken : fallback;

    internal static Scope Push(CancellationToken cancellationToken)
    {
        var scope = new Scope(_hasCurrentToken, _currentToken);
        _currentToken = cancellationToken;
        _hasCurrentToken = true;
        return scope;
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly bool _hadPreviousToken;
        private readonly CancellationToken _previousToken;

        internal Scope(bool hadPreviousToken, CancellationToken previousToken)
        {
            _hadPreviousToken = hadPreviousToken;
            _previousToken = previousToken;
        }

        public void Dispose()
        {
            _currentToken = _previousToken;
            _hasCurrentToken = _hadPreviousToken;
        }
    }
}
