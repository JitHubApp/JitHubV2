using System.Collections.Generic;

namespace MarkdownRenderer.Sample;

internal sealed record SampleGroup(
    string TitleResourceKey,
    string FallbackTitle,
    IReadOnlyList<SampleDefinition> Pages);
