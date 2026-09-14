namespace MarkdownRenderer.Math.Internal;

/// <summary>
/// Carries the current synchronous compilation token through the pinned parser
/// and typesetter, and bounds native parser recursion without adding either
/// concern to renderer-owned contracts.
/// Compilation is synchronous on one worker thread, so thread-local scope is
/// deterministic and avoids an allocation per checkpoint.
/// </summary>
internal static class MathCancellationScope
{
    [ThreadStatic]
    private static CancellationToken s_token;

    [ThreadStatic]
    private static bool s_hasToken;

    [ThreadStatic]
    private static int s_parserRecursionDepth;

    [ThreadStatic]
    private static int s_maximumParserRecursionDepth;

    [ThreadStatic]
    private static bool s_hasParserRecursionLimit;

    internal static Registration Enter(
        CancellationToken token,
        int maximumParserRecursionDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumParserRecursionDepth);

        var registration = new Registration(
            s_token,
            s_hasToken,
            s_parserRecursionDepth,
            s_maximumParserRecursionDepth,
            s_hasParserRecursionLimit);
        s_token = token;
        s_hasToken = true;
        s_parserRecursionDepth = 0;
        s_maximumParserRecursionDepth = maximumParserRecursionDepth;
        s_hasParserRecursionLimit = true;
        return registration;
    }

    internal static ParserRecursionRegistration EnterParserRecursion()
    {
        Checkpoint();
        if (!s_hasParserRecursionLimit)
            return default;

        if (s_parserRecursionDepth >= s_maximumParserRecursionDepth)
            throw new MathParserRecursionLimitException();

        int previousDepth = s_parserRecursionDepth;
        s_parserRecursionDepth++;
        return new ParserRecursionRegistration(previousDepth, hasLimit: true);
    }

    internal static void Checkpoint()
    {
        if (s_hasToken)
            s_token.ThrowIfCancellationRequested();
    }

    internal static CancellationToken CurrentToken =>
        s_hasToken ? s_token : CancellationToken.None;

    internal readonly struct Registration : IDisposable
    {
        private readonly CancellationToken _previousToken;
        private readonly bool _hadPreviousToken;
        private readonly int _previousParserRecursionDepth;
        private readonly int _previousMaximumParserRecursionDepth;
        private readonly bool _hadPreviousParserRecursionLimit;

        internal Registration(
            CancellationToken previousToken,
            bool hadPreviousToken,
            int previousParserRecursionDepth,
            int previousMaximumParserRecursionDepth,
            bool hadPreviousParserRecursionLimit)
        {
            _previousToken = previousToken;
            _hadPreviousToken = hadPreviousToken;
            _previousParserRecursionDepth = previousParserRecursionDepth;
            _previousMaximumParserRecursionDepth = previousMaximumParserRecursionDepth;
            _hadPreviousParserRecursionLimit = hadPreviousParserRecursionLimit;
        }

        public void Dispose()
        {
            s_token = _previousToken;
            s_hasToken = _hadPreviousToken;
            s_parserRecursionDepth = _previousParserRecursionDepth;
            s_maximumParserRecursionDepth = _previousMaximumParserRecursionDepth;
            s_hasParserRecursionLimit = _hadPreviousParserRecursionLimit;
        }
    }

    internal readonly struct ParserRecursionRegistration : IDisposable
    {
        private readonly int _previousDepth;
        private readonly bool _hasLimit;

        internal ParserRecursionRegistration(int previousDepth, bool hasLimit)
        {
            _previousDepth = previousDepth;
            _hasLimit = hasLimit;
        }

        public void Dispose()
        {
            if (_hasLimit)
                s_parserRecursionDepth = _previousDepth;
        }
    }
}

internal sealed class MathParserRecursionLimitException : Exception
{
    internal MathParserRecursionLimitException()
        : base("The formula exceeded the native parser recursion limit.")
    {
    }
}
