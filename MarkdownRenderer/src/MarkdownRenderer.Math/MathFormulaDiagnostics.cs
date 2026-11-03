using MarkdownRenderer.Math.Internal;

namespace MarkdownRenderer.Math;

/// <summary>Creates formula diagnostics with messages resolved through the host string provider.</summary>
internal static class MathFormulaDiagnostics
{
    internal static MathFormulaDiagnostic Create(
        IMathStringProvider? strings,
        string code,
        string stringKey,
        MathSourceRange range,
        string? languageTag,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string message = MathStringResolver.Resolve(strings, stringKey, languageTag);
        cancellationToken.ThrowIfCancellationRequested();
        return new MathFormulaDiagnostic(code, message, range);
    }
}
