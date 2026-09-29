using System.Text;

namespace MarkdownRenderer.PerformanceHarness;

internal static class PerformanceDocumentFactory
{
    internal const string ReadmeMixedCorpus = "readme-mixed-v1";
    internal const string PathologicalLongReferenceCorpus = "pathological-long-reference-token-v1";
    internal const string WarmScrollStressCorpus = "adversarial-nested-table-code-v1";

    // Roughly README-shaped rather than maximally AST-dense: prose dominates,
    // with recurring tables, lists, links, emphasis, and code. This keeps the
    // 1 MiB corpus representative of a large mixed document instead of turning
    // every 30 source characters into another retained layout object.
    private const string Section =
        "## Native rendering and semantic navigation\n\n" +
        "MarkdownRenderer keeps the reading surface responsive while **semantic text**, " +
        "[host-approved links](https://example.invalid/performance), inline `code`, source ranges, " +
        "and accessible roles remain available to keyboard, touch, pen, and automation clients. " +
        "The immutable document can be shared by multiple native views without reparsing.\n\n" +
        "The layout scheduler realizes viewport bands in the background and commits only the newest " +
        "generation. Tables use intrinsic columns, code remains horizontally readable, images are " +
        "resolved through host policy, and environment changes distinguish repaint from relayout. " +
        "This paragraph deliberately supplies normal prose density for a large README workload.\n\n" +
        "A warm scroll should reuse text layouts, source indexes, accessibility semantics, and cached " +
        "resources. It must not rebuild off-screen copy buttons or scan every block in the document. " +
        "Selection and hit testing preserve half-open UTF-16 mappings even when rendered text differs.\n\n" +
        "| Capability | Native behavior | Gate |\n" +
        "|:--|:--|--:|\n" +
        "| Tables | Intrinsic columns with local overflow | 120 Hz |\n" +
        "| Code | No-wrap, copy command, optional line numbers | 4 KiB |\n" +
        "| UIA | Stable semantic peers and text ranges | 100% |\n\n" +
        "- [x] Immutable semantic document\n" +
        "- [x] Indexed viewport realization\n" +
        "- [x] Cancellation-safe replacement\n" +
        "  - Nested content preserves ordinal and source semantics.\n\n" +
        "> Fluent defaults remain readable in Light, Dark, and High Contrast environments while app " +
        "> resources and immutable stylesheet rules provide deliberate customization.\n\n" +
        "```csharp\n" +
        "var engine = new MarkdownEngineBuilder()\n" +
        "    .UseProfile(MarkdownProfiles.GfmStrict)\n" +
        "    .Build();\n" +
        "var document = await engine.ParseAsync(source, cancellationToken);\n" +
        "view.Engine = engine;\n" +
        "view.Document = document;\n" +
        "```\n\n";

    internal static string Create(int utf16Bytes, int salt = 0)
    {
        if (utf16Bytes <= 0 || (utf16Bytes & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(utf16Bytes));

        int characterCount = utf16Bytes / sizeof(char);
        if (utf16Bytes >= 10 * 1024 * 1024)
            return CreatePathologicalReferenceDocument(characterCount, salt);

        var builder = new StringBuilder(characterCount, characterCount);
        string prefix = $"<!-- perf:{salt} -->\n";
        builder.Append(prefix.AsSpan(0, Math.Min(prefix.Length, characterCount)));
        while (builder.Length < characterCount)
        {
            int remaining = characterCount - builder.Length;
            builder.Append(Section.AsSpan(0, Math.Min(Section.Length, remaining)));
        }

        return builder.ToString();
    }

    internal static string GetCorpusName(int utf16Bytes)
        => utf16Bytes >= 10 * 1024 * 1024
            ? PathologicalLongReferenceCorpus
            : ReadmeMixedCorpus;

    /// <summary>
    /// Builds the 1 MiB warm-scroll corpus around three deliberately large
    /// containers. This prevents a fast top-level band index from hiding a
    /// linear scan inside a nested list, table, or fenced code block.
    /// </summary>
    internal static string CreateWarmScrollStress(int utf16Bytes)
    {
        if (utf16Bytes <= 0 || (utf16Bytes & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(utf16Bytes));

        int characterCount = utf16Bytes / sizeof(char);
        var builder = new StringBuilder(characterCount, characterCount);
        builder.Append("# Adversarial viewport-index corpus\n\n");
        builder.Append("One giant nested list, table, and code block exercise container-local viewport indexes.\n\n");

        for (int index = 0; index < 2_048; index++)
        {
            builder.Append("- parent item ").Append(index.ToString("D4")).Append(" with semantic text\n");
            if ((index & 1) == 0)
                builder.Append("  - nested item ").Append(index.ToString("D4")).Append(" remains virtualized\n");
        }

        builder.Append("\n| Row | Intrinsic content | Overflow value |\n");
        builder.Append("|---:|:---|---:|\n");
        for (int index = 0; index < 2_048; index++)
        {
            builder.Append('|').Append(index)
                .Append("|viewport-indexed table row ").Append(index.ToString("D4"))
                .Append("|0x").Append(index.ToString("X8")).Append("|\n");
        }

        builder.Append("\n```csharp\n");
        for (int index = 0; index < 5_000; index++)
        {
            builder.Append("var viewportLine").Append(index.ToString("D4"))
                .Append(" = ").Append(index).Append("; // indexed visual line\n");
        }
        builder.Append("```\n\n");

        if (builder.Length > characterCount)
        {
            throw new InvalidOperationException(
                $"Warm-scroll stress payload ({builder.Length:N0} chars) exceeds its " +
                $"{characterCount:N0}-character release corpus.");
        }

        const string filler =
            "Warm scrolling must reuse immutable layout bands without allocation or off-screen actions.\n\n";
        while (builder.Length + filler.Length <= characterCount)
            builder.Append(filler);
        if (builder.Length < characterCount)
            builder.Append(' ', characterCount - builder.Length);

        return builder.ToString();
    }

    /// <summary>
    /// Models the release gate's pathological 10 MiB input without conflating
    /// source size with an unsupported multi-million-DIP XAML surface. A small
    /// rendered viewport is followed by a very large reference-definition
    /// table. The parser must still consume and index every UTF-16 code unit,
    /// while first-paint work remains a valid viewer-first measurement.
    /// </summary>
    private static string CreatePathologicalReferenceDocument(int characterCount, int salt)
    {
        var builder = new StringBuilder(characterCount, characterCount);
        builder.Append("# Pathological reference document\n\n");
        builder.Append("The first viewport remains readable while the parser indexes a giant definition table.\n\n");

        int remaining = characterCount - builder.Length;
        string finalPrefix = $"[perf-{salt:x8}]: /target \"";
        const string finalSuffix = "\"\n";
        if (remaining >= finalPrefix.Length + finalSuffix.Length)
        {
            builder.Append(finalPrefix);
            builder.Append('a', remaining - finalPrefix.Length - finalSuffix.Length);
            builder.Append(finalSuffix);
        }
        else
        {
            builder.Append(' ', remaining);
        }

        return builder.ToString();
    }
}
