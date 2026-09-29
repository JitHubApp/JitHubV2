using System.Text.Json;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Conformance;

public sealed class ConformanceRunner
{
    public const string VerificationLevel =
        "official-reference-html-parity-and-deterministic-semantic-plan-integrity";
    public const string Limitation =
        "The HTML oracle enables raw HTML passthrough only while comparing official examples, " +
        "because production CommonMark and strict-GFM profiles intentionally render raw HTML literally. " +
        "That locked production security policy is verified independently.";

    public async Task<IReadOnlyList<ConformanceResult>> RunAsync(
        CorpusDefinition definition,
        IReadOnlyList<SpecExample> examples,
        ConformanceExceptionAllowlist allowlist,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(examples);
        ArgumentNullException.ThrowIfNull(allowlist);

        var engines = new Dictionary<string, MarkdownEngine>(StringComparer.Ordinal);
        var results = new List<ConformanceResult>(examples.Count);

        foreach (var example in examples.OrderBy(static value => value.Example))
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkdownEngine engine = GetEngine(definition, example, engines);
            string planHash = string.Empty;
            string expectedHtmlHash = HashNormalizedHtml(example.Html);
            string actualHtmlHash = string.Empty;
            string? failure = null;
            try
            {
                MarkdownDocument document = await engine.ParseAsync(example.Markdown, cancellationToken)
                    .ConfigureAwait(false);
                ValidateDocument(example, document);
                byte[] first = SemanticPlanSerializer.Serialize(definition.Profile, document);
                byte[] second = SemanticPlanSerializer.Serialize(definition.Profile, document);
                if (!first.AsSpan().SequenceEqual(second))
                    throw new InvalidDataException("Semantic plan serialization was not deterministic.");
                using var _ = JsonDocument.Parse(first);
                planHash = SemanticPlanSerializer.Sha256(first);

                string expectedHtml = NormalizeHtml(example.Html);
                string actualHtml = NormalizeHtml(
                    engine.RenderSpecificationHtmlForValidation(example.Markdown));
                actualHtmlHash = HashNormalizedHtml(actualHtml);
                if (!string.Equals(expectedHtml, actualHtml, StringComparison.Ordinal))
                {
                    int difference = FirstDifference(expectedHtml, actualHtml);
                    throw new InvalidDataException(
                        $"Official reference HTML differs at UTF-16 offset " +
                        $"{difference}. Expected SHA-256 {expectedHtmlHash}; actual {actualHtmlHash}. " +
                        $"Expected near difference: '{Excerpt(expectedHtml, difference)}'; " +
                        $"actual: '{Excerpt(actualHtml, difference)}'.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failure = $"{exception.GetType().Name}: {exception.Message}";
            }

            bool allowlisted = allowlist.TryGet(definition.Id, example.Example, out var exceptionEntry);
            ConformanceStatus status;
            string? issue = exceptionEntry?.Issue;
            if (failure is null && allowlisted)
            {
                status = ConformanceStatus.Fail;
                failure = $"Stale exception: {exceptionEntry!.Reason}";
            }
            else if (failure is not null && allowlisted)
            {
                status = ConformanceStatus.Exception;
                failure = $"{failure} Allowlisted: {exceptionEntry!.Reason}";
            }
            else
            {
                status = failure is null ? ConformanceStatus.Pass : ConformanceStatus.Fail;
            }

            results.Add(new ConformanceResult(
                definition.Id,
                example.Example,
                example.Section,
                status,
                planHash,
                expectedHtmlHash,
                actualHtmlHash,
                failure,
                issue));
        }

        return results.AsReadOnly();
    }

    private static MarkdownEngine GetEngine(
        CorpusDefinition definition,
        SpecExample example,
        Dictionary<string, MarkdownEngine> engines)
    {
        string key = example.Extensions.Count == 0
            ? string.Empty
            : string.Join('\u001f', example.Extensions);
        if (engines.TryGetValue(key, out MarkdownEngine? engine))
            return engine;

        MarkdownProfile profile = string.Equals(
            definition.Id,
            CorpusCatalog.Gfm029.Id,
            StringComparison.Ordinal)
                ? MarkdownProfiles.CreateGfmConformanceProfile(example.Extensions)
                : definition.Profile;
        engine = new MarkdownEngineBuilder()
            .UseProfile(profile)
            .WithParseCacheBudgetBytes(0)
            .Build();
        engines.Add(key, engine);
        return engine;
    }

    private static string NormalizeHtml(string value) =>
        (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string HashNormalizedHtml(string value) =>
        SemanticPlanSerializer.Sha256(System.Text.Encoding.UTF8.GetBytes(NormalizeHtml(value)));

    private static int FirstDifference(string expected, string actual)
    {
        int length = Math.Min(expected.Length, actual.Length);
        for (int index = 0; index < length; index++)
        {
            if (expected[index] != actual[index])
                return index;
        }

        return length;
    }

    private static string Excerpt(string value, int difference)
    {
        int start = Math.Max(0, difference - 24);
        int length = Math.Min(96, value.Length - start);
        return value.Substring(start, length)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
    }

    public static void ValidateDocument(SpecExample example, MarkdownDocument document)
    {
        if (!string.Equals(example.Markdown, document.Source, StringComparison.Ordinal))
            throw new InvalidDataException("The immutable document did not preserve the exact source.");
        if (document.HasErrors)
            throw new InvalidDataException("The parser reported an error diagnostic.");

        int previousStart = -1;
        foreach (var entry in document.SourceMap)
        {
            ValidateSpan(entry.SourceSpan, document.Source.Length, entry.Kind.ToString());
            if (entry.SourceSpan.Start < previousStart)
                throw new InvalidDataException("The immutable source map is not in source order.");
            if (entry.BlockIndex <= 0)
                throw new InvalidDataException("A source-map entry has a non-positive block index.");
            previousStart = entry.SourceSpan.Start;
        }

        foreach (var heading in document.GetHeadings())
        {
            ValidateSpan(heading.SourceSpan, document.Source.Length, "Heading");
            if (heading.Level is < 1 or > 6)
                throw new InvalidDataException($"Invalid heading level {heading.Level}.");
        }
        foreach (var link in document.GetLinks())
            ValidateSpan(link.SourceSpan, document.Source.Length, "Link");
        foreach (var codeBlock in document.GetCodeBlocks())
            ValidateSpan(codeBlock.SourceSpan, document.Source.Length, "CodeBlock");
        foreach (var image in document.GetImages())
            ValidateSpan(image.SourceSpan, document.Source.Length, "Image");
    }

    private static void ValidateSpan(SourceSpan span, int sourceLength, string kind)
    {
        if (span.Start < 0 || span.Length < 0 || span.Start > sourceLength - span.Length)
            throw new InvalidDataException($"{kind} has an invalid half-open UTF-16 source span.");
    }
}
