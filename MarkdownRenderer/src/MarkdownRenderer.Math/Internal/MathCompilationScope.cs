namespace MarkdownRenderer.Math.Internal;

/// <summary>
/// The cancellation token and native parser recursion budget of one
/// synchronous formula compilation. <see cref="CSharpMathSceneCompiler"/>
/// creates and owns one scope per formula.
/// </summary>
internal sealed class MathCompilationScope
{
    private readonly int _maximumParserRecursionDepth;
    private int _parserRecursionDepth;

    internal MathCompilationScope(CancellationToken token, int maximumParserRecursionDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumParserRecursionDepth);
        Token = token;
        _maximumParserRecursionDepth = maximumParserRecursionDepth;
    }

    internal CancellationToken Token { get; }

    internal void Checkpoint() => Token.ThrowIfCancellationRequested();

    /// <summary>
    /// Enters one parser frame. Disposing the result leaves it; entering past
    /// the configured depth throws <see cref="MathParserRecursionLimitException"/>.
    /// </summary>
    internal ParserRecursion EnterParserRecursion()
    {
        Checkpoint();
        if (_parserRecursionDepth >= _maximumParserRecursionDepth)
            throw new MathParserRecursionLimitException();

        int previousDepth = _parserRecursionDepth;
        _parserRecursionDepth++;
        return new ParserRecursion(this, previousDepth);
    }

    /// <summary>One entered parser frame; a default value enters nothing.</summary>
    internal readonly struct ParserRecursion : IDisposable
    {
        private readonly MathCompilationScope? _scope;
        private readonly int _previousDepth;

        internal ParserRecursion(MathCompilationScope scope, int previousDepth)
        {
            _scope = scope;
            _previousDepth = previousDepth;
        }

        public void Dispose()
        {
            if (_scope is not null)
                _scope._parserRecursionDepth = _previousDepth;
        }
    }
}
