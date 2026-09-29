namespace MarkdownRenderer.Conformance;

public static class CorpusCatalog
{
    public static CorpusDefinition CommonMark0312 { get; } = new(
        Id: "commonmark-0.31.2",
        SpecificationVersion: "0.31.2",
        SpecificationUri: new Uri("https://spec.commonmark.org/0.31.2/"),
        SourceUri: new Uri("https://spec.commonmark.org/0.31.2/spec.json"),
        SourceSha256: "d431b29d97b6f73e69d547109cf5081578fac931e72afe95639ebe766c1b2a20",
        SourceFormat: CorpusSourceFormat.SpecJson,
        Profile: MarkdownProfiles.CommonMark);

    // GitHub's pinned 0.29-gfm distribution publishes the normative examples
    // in annotated spec.txt form, rather than as spec.json. CorpusStore verifies
    // the official bytes first, then converts them to the same normalized model.
    public static CorpusDefinition Gfm029 { get; } = new(
        Id: "gfm-0.29",
        SpecificationVersion: "0.29-gfm",
        SpecificationUri: new Uri("https://github.github.com/gfm/"),
        SourceUri: new Uri("https://raw.githubusercontent.com/github/cmark-gfm/0.29.0.gfm.13/test/spec.txt"),
        SourceSha256: "7d8e5814befec287ac116786d81ff14e0adc9b13295b4494649e995408fd871c",
        SourceFormat: CorpusSourceFormat.AnnotatedSpec,
        Profile: MarkdownProfiles.GfmStrict);

    public static IReadOnlyList<CorpusDefinition> All { get; } =
        Array.AsReadOnly([CommonMark0312, Gfm029]);

    public static CorpusDefinition Resolve(string id)
        => All.FirstOrDefault(value => string.Equals(value.Id, id, StringComparison.OrdinalIgnoreCase))
           ?? throw new ArgumentException($"Unknown conformance suite '{id}'.", nameof(id));
}
