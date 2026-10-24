using System.Text;
using Xunit;

namespace MarkdownRenderer.Math.Tests;

public sealed class MathFormulaFuzzTests
{
    [Fact]
    public async Task ArbitraryTex_EitherProducesFiniteBoundedSceneOrExactFallback()
    {
        var random = new Random(0x54_45_58);
        var processor = new MathFormulaProcessor();
        var options = new MathProcessingOptions(
            maximumTexLength: 512,
            maximumNestingDepth: 24,
            maximumSceneCommands: 4096,
            maximumWorkingMemoryBytes: 4 * 1024 * 1024,
            maximumProcessingTime: TimeSpan.FromMilliseconds(100));

        for (int iteration = 0; iteration < 320; iteration++)
        {
            string tex = CreateArbitraryTex(random, random.Next(0, 513));
            string original = "$" + tex + "$";
            var request = new MathFormulaRequest(
                original,
                tex,
                new MathSourceRange(0, original.Length),
                new MathSourceRange(1, tex.Length),
                MathFormulaDisplayMode.Inline);

            MathFormulaResult result = await processor.ProcessAsync(request, options);

            Assert.Same(request, result.Request);
            if (result.Scene is MathScene scene)
            {
                Assert.True(float.IsFinite(scene.Width) && scene.Width >= 0);
                Assert.True(float.IsFinite(scene.Height) && scene.Height >= 0);
                Assert.True(float.IsFinite(scene.Baseline));
                Assert.InRange(scene.Baseline, 0, scene.Height);
                Assert.InRange(scene.Commands.Count, 0, options.MaximumSceneCommands);
                Assert.Null(result.FallbackSource);
            }
            else
            {
                Assert.Equal(original, result.FallbackSource);
                Assert.NotNull(result.Diagnostic);
            }
        }
    }

    private static string CreateArbitraryTex(Random random, int targetLength)
    {
        string[] tokens =
        [
            "x", "1", "+", "-", "=", "{", "}", "[", "]", "(", ")", "^", "_", "&", "#",
            "\\frac", "\\sqrt", "\\sum", "\\int", "\\left", "\\right", "\\begin", "\\end",
            "\\notacommand", "\\unicode", "\\text", "\\over", " ", "\t", "\r", "\n", "\0",
            "😀", "e\u0301", "\uD800", "\uDC00",
        ];
        var builder = new StringBuilder(targetLength + 16);
        while (builder.Length < targetLength)
        {
            string token = tokens[random.Next(tokens.Length)];
            int remaining = targetLength - builder.Length;
            builder.Append(token.AsSpan(0, System.Math.Min(token.Length, remaining)));
        }

        return builder.ToString();
    }
}
