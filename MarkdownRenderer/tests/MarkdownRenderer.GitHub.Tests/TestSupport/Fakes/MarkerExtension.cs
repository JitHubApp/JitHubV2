using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Theming;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ProgressiveSceneDeferralFixture;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;

/// <summary>
/// A paragraph extension that emits a code block carrying a unique
/// deferred scene marker of the given kind, twice with the same source
/// span when asked for duplicates, or plain materialized text when the
/// context cannot defer offscreen scenes.
/// </summary>
internal sealed class MarkerExtension(string markerKind, bool duplicateSpans) : IMarkdownExtension
{
    private int _nextToken;

    public string Id => "MarkdownRenderer.GitHub.Tests.Marker";

    public void Configure(MarkdownExtensionBuilder builder)
        => builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, RenderAsync);

    private ValueTask RenderAsync(MarkdownExtensionContext context, MarkdownContentBuilder content)
    {
        if (!context.CanDeferOffscreenScenes)
        {
            content.AddText("materialized", context.Node.SourceSpan);
            return ValueTask.CompletedTask;
        }

        int count = duplicateSpans ? 2 : 1;
        for (int i = 0; i < count; i++)
        {
            string marker = string.Concat(markerKind, "|", Interlocked.Increment(ref _nextToken)
                .ToString(System.Globalization.CultureInfo.InvariantCulture));
            content.AddCodeBlock(
                context.Node.Literal ?? string.Empty,
                "test",
                context.Node.SourceSpan,
                MarkdownStyleRole.CodeBlock,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [MarkerAttribute] = marker,
                });
        }

        return ValueTask.CompletedTask;
    }
}
