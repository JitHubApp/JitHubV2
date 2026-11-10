using System;

namespace MarkdownRenderer.Sample.Models.Samples;

internal sealed record SampleDefinition(
    string Key,
    string TitleResourceKey,
    string FallbackTitle,
    string IconGlyph,
    Func<string> SourceFactory);
