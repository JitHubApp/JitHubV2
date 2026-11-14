using MarkdownRenderer.Math.Internal.Compilation;

namespace MarkdownRenderer.Math.Internal;

/// <summary>
/// Bridges the vendored CSharpMath and Typography code to the
/// <see cref="MathCompilationScope"/> of the compilation running on the
/// current thread.
/// </summary>
/// <remarks>
/// This is the one ambient context in the math pack, kept on purpose. The
/// vendored snapshot under <c>ThirdParty/</c> is pinned by
/// <c>ThirdParty/PROVENANCE.json</c> (<c>patchedAggregateSha256</c>), and its
/// patch points call these static members from deep inside the parser, the
/// typesetter and the font reader, none of which can take the scope as a
/// parameter without re-patching that snapshot. The only state here is one
/// thread-static reference to the current scope; the scope's token and
/// recursion depth live on the instance the compiler owns. Compilation is
/// synchronous on one worker thread, so the reference is deterministic.
/// Removal plan: when the vendored snapshot is next re-patched, pass the scope
/// through the parser, typesetter and font loading and delete this bridge.
/// </remarks>
internal static class MathCancellationScope
{
    [ThreadStatic]
    private static MathCompilationScope? s_current;

    internal static CancellationToken CurrentToken =>
        s_current?.Token ?? CancellationToken.None;

    /// <summary>Makes <paramref name="scope"/> current until the result is disposed.</summary>
    internal static Registration Enter(MathCompilationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var registration = new Registration(s_current);
        s_current = scope;
        return registration;
    }

    internal static MathCompilationScope.ParserRecursion EnterParserRecursion() =>
        s_current?.EnterParserRecursion() ?? default;

    internal static void Checkpoint() => s_current?.Checkpoint();

    internal readonly struct Registration : IDisposable
    {
        private readonly MathCompilationScope? _previous;

        internal Registration(MathCompilationScope? previous) => _previous = previous;

        public void Dispose() => s_current = _previous;
    }
}
