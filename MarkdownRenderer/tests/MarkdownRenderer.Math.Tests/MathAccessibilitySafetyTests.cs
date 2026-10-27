using System.Collections.Concurrent;
using Xunit;

namespace MarkdownRenderer.Math.Tests;

public sealed class MathAccessibilitySafetyTests
{
    [Fact]
    public async Task DirectFormatter_DeepNestingFallsBackWithoutRecursiveOverflow()
    {
        string tex = new string('{', 1_024) + "x" + new string('}', 1_024);
        MathFormulaRequest request = CreateRequest(tex);

        MathAccessibilityDescription result = await new DefaultMathAccessibilityFormatter()
            .FormatAsync(request);

        Assert.Same(tex, result.CopyText);
        Assert.Equal(tex, result.StructuralSpeech);
        Assert.True(result.StructuralSpeech.Length <= 4_096);
    }

    [Fact]
    public async Task DirectFormatter_DoesNotResolveClosingPhraseAfterDepthLimit()
    {
        string tex = string.Concat(Enumerable.Repeat(@"\sqrt", 256)) + "x";
        var strings = new AmplifyingCountingStringProvider();

        MathAccessibilityDescription result = await new DefaultMathAccessibilityFormatter(strings)
            .FormatAsync(CreateRequest(tex));

        Assert.Equal(tex, result.StructuralSpeech);
        Assert.Equal(1, strings.Count(MathStringKeys.SquareRoot));
        Assert.Equal(0, strings.Count(MathStringKeys.EndRoot));
    }

    [Fact]
    public async Task DirectFormatter_BoundsLocalizedSpeechAndResolvesEachKeyOnce()
    {
        string tex = new('+', 5_000);
        var strings = new AmplifyingCountingStringProvider();

        MathAccessibilityDescription result = await new DefaultMathAccessibilityFormatter(strings)
            .FormatAsync(CreateRequest(tex));

        Assert.True(result.StructuralSpeech.Length <= 4_096);
        Assert.EndsWith("\u2026", result.StructuralSpeech, StringComparison.Ordinal);
        Assert.Equal(1, strings.Count(MathStringKeys.Plus));
        Assert.Equal(1, strings.Count(MathStringKeys.OriginalTex));
        Assert.Equal(1, strings.Count(MathStringKeys.AutomationName));
    }

    [Fact]
    public async Task DirectFormatter_BoundsHelpTextAndPreservesExactCopyText()
    {
        string tex = new('x', 100_000);
        MathFormulaRequest request = CreateRequest(tex);

        MathAccessibilityDescription result = await new DefaultMathAccessibilityFormatter()
            .FormatAsync(request);

        Assert.Same(tex, result.CopyText);
        Assert.True(result.HelpText.Length <= 4_096);
        Assert.EndsWith("\u2026", result.HelpText, StringComparison.Ordinal);
        Assert.True(result.StructuralSpeech.Length <= 4_096);
    }

    [Fact]
    public async Task DirectFormatter_TruncationDoesNotSplitSourceSurrogatePair()
    {
        string tex = new string('x', 4_094) + "\U0001F600" + "tail";

        MathAccessibilityDescription result = await new DefaultMathAccessibilityFormatter()
            .FormatAsync(CreateRequest(tex));

        Assert.EndsWith("\u2026", result.StructuralSpeech, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(result.StructuralSpeech[^2]));
    }

    [Fact]
    public async Task DirectFormatter_HelpTruncationDoesNotSplitSourceSurrogatePair()
    {
        // The built-in "Original TeX: " prefix consumes fourteen code units,
        // leaving 4,081 before the reserved ellipsis position.
        string tex = new string('x', 4_080) + "\U0001F600" + "tail";

        MathAccessibilityDescription result = await new DefaultMathAccessibilityFormatter()
            .FormatAsync(CreateRequest(tex));

        Assert.EndsWith("\u2026", result.HelpText, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(result.HelpText[^2]));
    }

    [Fact]
    public async Task DirectFormatter_PropagatesCancellationBeforeScanning()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new DefaultMathAccessibilityFormatter().FormatAsync(
                CreateRequest("\\" + new string('a', 60_000)),
                cancellation.Token));
    }

    private static MathFormulaRequest CreateRequest(string tex) =>
        new(
            tex,
            tex,
            new MathSourceRange(0, tex.Length),
            new MathSourceRange(0, tex.Length),
            MathFormulaDisplayMode.Display);

    private sealed class AmplifyingCountingStringProvider : IMathStringProvider
    {
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

        internal int Count(string key) => _counts.TryGetValue(key, out int count) ? count : 0;

        public string? GetString(string resourceKey, string? languageTag)
        {
            _counts.AddOrUpdate(resourceKey, 1, static (_, count) => count + 1);
            return resourceKey == MathStringKeys.Plus
                ? new string('p', 1_024)
                : null;
        }
    }
}
