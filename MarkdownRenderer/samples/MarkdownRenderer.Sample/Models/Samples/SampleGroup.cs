using System.Collections.Generic;

namespace MarkdownRenderer.Sample.Models.Samples;

internal sealed record SampleGroup(
    string TitleResourceKey,
    string FallbackTitle,
    IReadOnlyList<SampleDefinition> Pages);
