using MarkdownRenderer.Math.Accessibility;

namespace MarkdownRenderer.Math.Tests;

/// <summary>An accessibility formatter that counts its calls and echoes the TeX source.</summary>
internal sealed class CountingAccessibilityFormatter : IMathAccessibilityFormatter
{
    private int _callCount;

    internal int CallCount => Volatile.Read(ref _callCount);

    public ValueTask<MathAccessibilityDescription> FormatAsync(
        MathFormulaRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        return ValueTask.FromResult(new MathAccessibilityDescription(
            "math",
            request.TexSource,
            request.TexSource,
            request.TexSource));
    }
}
