using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Phase 3 oracles for schema-3 correctness inputs. Each compares what the
/// parsed document asked the native view to show (the input's verified
/// inventory and image demand) with what the live app exposed.
/// Content inside GitHub disclosures is collapsed by default, so it is not
/// required to be visible; it is excluded rather than assumed either way.
/// </summary>
internal static class ReadmeCorrectnessOracles
{
    internal const string ContentCompleteness = "contentCompleteness";
    internal const string UnsupportedSyntaxClassification = "unsupportedSyntaxClassification";
    internal const string ResourceCoverage = "resourceCoverage";

    internal sealed record Span(int Start, int Length)
    {
        internal bool Contains(Span other) =>
            other.Start >= Start && other.Start + other.Length <= Start + Length;
    }

    internal sealed record TextBlock(string Kind, Span Span, string Text);

    internal sealed record ExpectedContent(
        IReadOnlyList<TextBlock> TextBlocks,
        int TableCount,
        IReadOnlyList<Span> TaskItems,
        IReadOnlyList<Span> Disclosures,
        IReadOnlyList<string> UnrecognizedInlineTypes,
        IReadOnlyList<(string Code, string Severity)> Diagnostics);

    internal sealed record ObservedContent(
        string Text,
        int Headings,
        int Tables,
        int TaskCheckboxes,
        int Disclosures,
        int UnavailableImages,
        int LoadingImages);

    internal sealed record ImageDemandSummary(int Stored, int DataUri, int UpstreamUnavailable, int OtherTerminal, IReadOnlyDictionary<string, int> States)
    {
        /// <summary>Demanded occurrences per authored source hash.</summary>
        internal IReadOnlyDictionary<string, int> OccurrencesBySource { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Sources whose demand records no capturable bytes upstream.</summary>
        internal IReadOnlySet<string> SourcesWithoutBytes { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The app's own per-image failure log, by authored source hash: sources it
    /// refused as documented-unsupported SVG content although their bytes were
    /// verified, and every other unavailable source.
    /// </summary>
    internal sealed record ObservedImageFailures(IReadOnlySet<string> UnsupportedContentSources, IReadOnlySet<string> OtherSources);

    /// <summary>
    /// Reads <c>image-unavailable.ndjson</c> beside <c>image-resolution.ndjson</c>
    /// in the app's runtime directory. Returns null when the log is absent, so
    /// the oracle keeps its count-only rule.
    /// </summary>
    internal static ObservedImageFailures? LoadObservedImageFailures(string runtimeDirectory)
    {
        string unavailablePath = Path.Combine(runtimeDirectory, "image-unavailable.ndjson");
        if (!File.Exists(unavailablePath))
            return null;
        var withBytes = new HashSet<string>(StringComparer.Ordinal);
        string resolutionPath = Path.Combine(runtimeDirectory, "image-resolution.ndjson");
        if (File.Exists(resolutionPath))
        {
            foreach (string line in File.ReadLines(resolutionPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                using JsonDocument row = JsonDocument.Parse(line);
                if (row.RootElement.TryGetProperty("HasAsset", out JsonElement hasAsset) && hasAsset.ValueKind == JsonValueKind.True &&
                    row.RootElement.TryGetProperty("SourceSha256", out JsonElement source) && source.GetString() is { } hash)
                {
                    withBytes.Add(hash);
                }
            }
        }

        var unsupported = new HashSet<string>(StringComparer.Ordinal);
        var other = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(unavailablePath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using JsonDocument row = JsonDocument.Parse(line);
            if (!row.RootElement.TryGetProperty("SourceSha256", out JsonElement sourceElement) || sourceElement.GetString() is not { } source)
                continue;
            bool unsupportedSvg = row.RootElement.TryGetProperty("SvgFailureReason", out JsonElement reason) &&
                reason.ValueKind == JsonValueKind.String &&
                string.Equals(reason.GetString(), "UnsupportedContent", StringComparison.Ordinal);
            if (unsupportedSvg && withBytes.Contains(source))
                unsupported.Add(source);
            else
                other.Add(source);
        }
        other.ExceptWith(unsupported);
        return new ObservedImageFailures(unsupported, other);
    }

    internal static ExpectedContent LoadExpectedContent(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        JsonElement inventory = root.GetProperty("inventory");
        if (!inventory.GetProperty("available").GetBoolean())
            throw new InvalidDataException("The expected-content inventory reports that no parsed tree was available.");

        var blocks = inventory.GetProperty("textBlocks").EnumerateArray()
            .Select(block => new TextBlock(
                block.GetProperty("kind").GetString() ?? string.Empty,
                ReadSpan(block),
                block.GetProperty("text").GetString() ?? string.Empty))
            .ToArray();
        return new ExpectedContent(
            blocks,
            inventory.GetProperty("tables").GetArrayLength(),
            inventory.GetProperty("taskItems").EnumerateArray().Select(ReadSpan).ToArray(),
            inventory.GetProperty("disclosures").EnumerateArray().Select(ReadSpan).ToArray(),
            inventory.GetProperty("unrecognizedInlineTypes").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray(),
            root.GetProperty("diagnostics").EnumerateArray()
                .Select(item => (item.GetProperty("code").GetString() ?? string.Empty, item.GetProperty("severity").GetString() ?? string.Empty))
                .ToArray());
    }

    internal static ImageDemandSummary LoadImageDemand(string manifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var withoutBytes = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement entry in document.RootElement.GetProperty("imageDemand").EnumerateArray())
        {
            string state = entry.GetProperty("state").GetString() ?? "unknown";
            states[state] = states.GetValueOrDefault(state) + 1;
            if (entry.TryGetProperty("sourceSha256", out JsonElement sourceElement) && sourceElement.GetString() is { } source)
            {
                occurrences[source] = occurrences.GetValueOrDefault(source) + 1;
                if (state is not ("stored" or "data-uri"))
                    withoutBytes.Add(source);
            }
        }

        int stored = states.GetValueOrDefault("stored");
        int dataUri = states.GetValueOrDefault("data-uri");
        int upstream = states.GetValueOrDefault("upstream-unavailable");
        int other = states.Where(pair => pair.Key is not ("stored" or "data-uri" or "upstream-unavailable")).Sum(pair => pair.Value);
        return new ImageDemandSummary(stored, dataUri, upstream, other, states)
        {
            OccurrencesBySource = occurrences,
            SourcesWithoutBytes = withoutBytes,
        };
    }

    /// <summary>
    /// Every visible text block must appear, in document order, as an ordered
    /// token subsequence of the app's UIA document text; tables, task items and
    /// disclosures outside collapsed disclosures must each be observed.
    /// </summary>
    internal static CorrectnessCheck EvaluateContentCompleteness(ExpectedContent expected, ObservedContent observed)
    {
        if (expected.UnrecognizedInlineTypes.Count > 0)
        {
            return CorrectnessCheck.NotEvaluated(ContentCompleteness,
                "The inventory contains unrecognized inline types, so expected text is incomplete: " +
                string.Join(", ", expected.UnrecognizedInlineTypes));
        }

        TextBlock[] visible = expected.TextBlocks
            .Where(block => !expected.Disclosures.Any(disclosure => disclosure.Contains(block.Span)))
            .ToArray();
        string[] observedTokens = Tokenize(observed.Text);
        int cursor = 0;
        int matchedTokens = 0;
        int expectedTokens = 0;
        foreach (TextBlock block in visible)
        {
            foreach (string token in Tokenize(block.Text))
            {
                expectedTokens++;
                while (cursor < observedTokens.Length && !string.Equals(observedTokens[cursor], token, StringComparison.Ordinal))
                    cursor++;
                if (cursor == observedTokens.Length)
                {
                    return CorrectnessCheck.Failed(ContentCompleteness,
                        $"Visible {block.Kind} text at source [{block.Span.Start}, {block.Span.Start + block.Span.Length}) is missing or out of order " +
                        $"in the app's document text after {matchedTokens}/{expectedTokens} expected tokens.");
                }
                cursor++;
                matchedTokens++;
            }
        }

        int visibleTasks = expected.TaskItems.Count(task => !expected.Disclosures.Any(disclosure => disclosure.Contains(task)));
        int topLevelDisclosures = expected.Disclosures.Count(disclosure =>
            !expected.Disclosures.Any(other => other != disclosure && other.Contains(disclosure)));
        var shortfalls = new List<string>();
        if (observed.Tables < expected.TableCount)
            shortfalls.Add($"tables {observed.Tables}/{expected.TableCount}");
        if (observed.TaskCheckboxes < visibleTasks)
            shortfalls.Add($"task checkboxes {observed.TaskCheckboxes}/{visibleTasks}");
        if (observed.Disclosures < topLevelDisclosures)
            shortfalls.Add($"disclosures {observed.Disclosures}/{topLevelDisclosures}");
        if (shortfalls.Count > 0)
        {
            return CorrectnessCheck.Failed(ContentCompleteness,
                "The app exposed fewer structures than the document contains: " + string.Join("; ", shortfalls) + ".");
        }

        return CorrectnessCheck.Passed(ContentCompleteness,
            $"All {visible.Length} visible text blocks ({expectedTokens} tokens) appear in order in the app's document text; " +
            $"tables {observed.Tables}≥{expected.TableCount}, task checkboxes {observed.TaskCheckboxes}≥{visibleTasks}, " +
            $"disclosures {observed.Disclosures}≥{topLevelDisclosures}. Disclosure bodies are collapsed by default and excluded.");
    }

    /// <summary>
    /// Engine diagnostics are the per-document unsupported-syntax inventory.
    /// Their presence is a classification, never a rendered-content pass.
    /// </summary>
    internal static CorrectnessCheck EvaluateUnsupportedSyntax(ExpectedContent expected)
    {
        if (expected.Diagnostics.Count == 0)
        {
            return CorrectnessCheck.Passed(UnsupportedSyntaxClassification,
                "The engine reported no unsupported-syntax or fallback diagnostics for this document.");
        }

        string summary = string.Join(", ", expected.Diagnostics
            .GroupBy(diagnostic => $"{diagnostic.Code}/{diagnostic.Severity}")
            .Select(group => $"{group.Key}×{group.Count()}"));
        return CorrectnessCheck.Unsupported(UnsupportedSyntaxClassification,
            $"Classified unsupported syntax with documented fallback: {summary}.");
    }

    /// <summary>
    /// Every demanded image must reach a terminal state. An image whose bytes
    /// were captured must not be reported unavailable; images without bytes
    /// are classified by their recorded upstream or terminal state.
    /// </summary>
    internal static CorrectnessCheck EvaluateResourceCoverage(
        ImageDemandSummary demand,
        int unavailable,
        int loading,
        ObservedImageFailures? failures = null)
    {
        if (loading > 0)
            return CorrectnessCheck.Failed(ResourceCoverage, $"{loading} image(s) were still loading after full traversal.");

        // An image omitted because the audit input hit its own budget exists
        // upstream; it can be neither an upstream classification nor a pass.
        if (demand.States.GetValueOrDefault("resource-limit") is int omitted and > 0)
        {
            return CorrectnessCheck.NotEvaluated(ResourceCoverage,
                $"{omitted} demanded image(s) were omitted by the correctness input's own size budget; recapture the input.");
        }

        if (failures is not null)
            return EvaluateResourceCoverageBySource(demand, unavailable, failures);

        int withoutBytes = demand.UpstreamUnavailable + demand.OtherTerminal;
        if (unavailable > withoutBytes)
        {
            return CorrectnessCheck.Failed(ResourceCoverage,
                $"{unavailable} image(s) were unavailable, but only {withoutBytes} demanded image(s) lack captured bytes; " +
                "a captured image failed to load or render.");
        }

        string states = string.Join(", ", demand.States.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        if (withoutBytes > 0)
        {
            return CorrectnessCheck.UpstreamUnavailable(ResourceCoverage,
                $"{withoutBytes} demanded image(s) have no capturable bytes upstream ({states}); {unavailable} unavailable image(s) observed.");
        }

        return CorrectnessCheck.Passed(ResourceCoverage,
            $"All {demand.Stored + demand.DataUri} demanded image(s) had verified bytes and none were unavailable or loading ({states}).");
    }

    /// <summary>
    /// Word runs and single punctuation marks. The inventory omits atomic
    /// inline objects (an image contributes no paragraph text) while the app
    /// exposes their alt text, so whitespace tokens could glue a stray mark
    /// onto a neighbouring word (`search/ .` versus `search/.`). Words must
    /// still appear exactly and in order.
    /// </summary>
    /// <summary>
    /// P3-004: every unavailable image must be explained by its source: either
    /// its demand records no capturable bytes upstream, or the app refused its
    /// verified SVG bytes as documented-unsupported content (animation,
    /// foreignObject, scripts). Any other unavailable source fails, and the
    /// observed count may not exceed the demanded occurrences of explained
    /// sources. Documented-unsupported content is classified, never passed.
    /// </summary>
    private static CorrectnessCheck EvaluateResourceCoverageBySource(
        ImageDemandSummary demand,
        int unavailable,
        ObservedImageFailures failures)
    {
        var unexplained = failures.OtherSources.Where(source => !demand.SourcesWithoutBytes.Contains(source)).ToArray();
        if (unexplained.Length > 0)
        {
            return CorrectnessCheck.Failed(ResourceCoverage,
                $"{unexplained.Length} image source(s) with captured bytes failed to load or render for a reason other than documented-unsupported SVG content.");
        }

        var explained = new HashSet<string>(demand.SourcesWithoutBytes, StringComparer.Ordinal);
        explained.UnionWith(failures.UnsupportedContentSources);
        int explainedOccurrences = explained.Sum(source => demand.OccurrencesBySource.GetValueOrDefault(source));
        if (unavailable > explainedOccurrences)
        {
            return CorrectnessCheck.Failed(ResourceCoverage,
                $"{unavailable} image(s) were unavailable, but demanded occurrences of explained sources account for only {explainedOccurrences}.");
        }

        string states = string.Join(", ", demand.States.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        if (failures.UnsupportedContentSources.Count > 0)
        {
            return CorrectnessCheck.Unsupported(ResourceCoverage,
                $"{failures.UnsupportedContentSources.Count} captured SVG source(s) use documented-unsupported content (animation, foreignObject or scripts) " +
                $"and show the typed placeholder; {unavailable} unavailable image(s) observed ({states}).");
        }

        int withoutBytes = demand.UpstreamUnavailable + demand.OtherTerminal;
        if (withoutBytes > 0)
        {
            return CorrectnessCheck.UpstreamUnavailable(ResourceCoverage,
                $"{withoutBytes} demanded image(s) have no capturable bytes upstream ({states}); {unavailable} unavailable image(s) observed.");
        }

        return unavailable == 0
            ? CorrectnessCheck.Passed(ResourceCoverage,
                $"All {demand.Stored + demand.DataUri} demanded image(s) had verified bytes and none were unavailable or loading ({states}).")
            : CorrectnessCheck.Failed(ResourceCoverage,
                $"{unavailable} image(s) were unavailable although every demanded image had verified bytes.");
    }

    internal static string[] Tokenize(string text) =>
        TokenPattern.Matches(text).Select(match => match.Value).ToArray();

    private static readonly Regex TokenPattern = new(
        @"[\p{L}\p{M}\p{N}]+|[^\s\p{L}\p{M}\p{N}]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// UIA elements the renderer selects as one unit: a text range inside an
    /// image's alt text, a formula, a diagram or a hosted element selects the
    /// whole object, so a bounded selection round trip must use ordinary text.
    /// </summary>
    internal static bool IsAtomicSelectionObject(string? automationId) =>
        automationId is not null &&
        (automationId.StartsWith("MarkdownImage", StringComparison.Ordinal) ||
         automationId.StartsWith("MarkdownLinkedImage", StringComparison.Ordinal) ||
         automationId.StartsWith("MarkdownMath", StringComparison.Ordinal) ||
         automationId.StartsWith("MarkdownDiagram", StringComparison.Ordinal) ||
         automationId.StartsWith("MarkdownHosted", StringComparison.Ordinal) ||
         string.Equals(automationId, "MarkdownEmbed", StringComparison.Ordinal));

    private static Span ReadSpan(JsonElement element) =>
        new(element.GetProperty("sourceStart").GetInt32(), element.GetProperty("sourceLength").GetInt32());
}
