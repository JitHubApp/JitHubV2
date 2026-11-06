namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Markdown sources for the root-local table relayout tests: the standard
/// four-column image table, the three-table mixed batch, generated tables for
/// the placement caps, and the two-image SVG table.
/// </summary>
internal static class RootLocalTableSources
{
    internal static string CreateStandard(RootLocalTableResolver resolver, bool forceOverflow = false) =>
        "# Table image placement\n\n" +
        "| **Item** | **Preview** | **Details** | **Notes** |\n" +
        "| :--- | ---: | :--- | :--- |\n" +
        $"| [wide entry](https://example.test/wide) | ![wide aspect image]({resolver.WideUrl}) | " +
        "**Available** with [reference](https://example.test/reference) and `inline code` | " +
        (forceOverflow
            ? "UnbreakableColumnMinimumTokenABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ"
            : "Breakable note text keeps the preferred widths available for responsive column rebalancing") + " |\n" +
        $"| Tall item | ![tall aspect image]({resolver.TallUrl}) | *Compact* [details](https://example.test/details) | short note |\n" +
        $"| Square item | ![square preview]({resolver.SquareUrl}) | Body text only |  |\n" +
        "| Empty preview |  | Empty image cell beside ordinary text |  |\n";

    /// <summary>
    /// The standard table, then a sibling image whose resolve never completes,
    /// ninety paragraphs, and a distant image that stays estimated under a
    /// lazy viewport.
    /// </summary>
    internal static string CreateLazyWithPendingAndFarImages(RootLocalTableResolver resolver) =>
        CreateStandard(resolver) +
        $"\n\n![pending sibling]({resolver.PendingUrl})\n\n" +
        string.Join("\n\n", Enumerable.Range(0, 90).Select(static index =>
            $"Lazy table sibling {index}: preserve the measured mask and image work.")) +
        $"\n\n![far estimated image]({resolver.FarUrl})\n";

    internal static string CreateThreeTableMixed(RootLocalTableResolver resolver) =>
        "# Mixed measured roots\n\n" +
        "| **Wide item** | **Preview** |\n| --- | --- |\n" +
        $"| [first](https://example.test/first) | ![wide table image]({resolver.WideUrl}) |\n\n" +
        $"A paragraph with [a link](https://example.test/paragraph) and an inline image " +
        $"![inline paragraph image]({resolver.InlineUrl}) remains in the same document flow.\n\n" +
        "| **Tall item** | **Preview** |\n| --- | --- |\n" +
        $"| second row | ![tall table image]({resolver.TallUrl}) |\n\n" +
        $"![standalone image]({resolver.StandaloneUrl})\n\n" +
        "| **Square item** | **Preview** |\n| --- | --- |\n" +
        $"| third row | ![square table image]({resolver.SquareUrl}) |\n";

    internal static string CreateGenerated(
        RootLocalTableResolver resolver,
        int columns,
        int bodyRows,
        bool images)
    {
        string[] header = Enumerable.Range(0, columns).Select(static column => $"Heading {column}").ToArray();
        string[] separators = Enumerable.Repeat("---", columns).ToArray();
        var lines = new List<string>
        {
            "# Bounded table placement\n",
            "| " + string.Join(" | ", header) + " |",
            "| " + string.Join(" | ", separators) + " |",
        };
        for (int row = 0; row < bodyRows; row++)
        {
            var cells = new string[columns];
            for (int column = 0; column < columns; column++)
            {
                int imageIndex = row * columns + column;
                cells[column] = images && imageIndex < resolver.GeneratedImageCount
                    ? $"![generated {imageIndex}]({resolver.GeneratedUrl(imageIndex)})"
                    : $"cell{row}_{column}";
            }
            lines.Add("| " + string.Join(" | ", cells) + " |");
        }
        return string.Join("\n", lines) + "\n";
    }

    internal static string CreateSvg(SvgTableResolver resolver) =>
        "| Resource | Preview | Details |\n" +
        "| --- | --- | --- |\n" +
        $"| [Wide](https://example.test/wide) | ![wide SVG alt]({resolver.WideUrl}) | " +
        "**Measured** [wide details](https://example.test/wide-details) with `code` |\n" +
        $"| Tall resource | ![tall SVG alt]({resolver.TallUrl}) | " +
        "*Tall detail* and [second link](https://example.test/tall-details) |\n" +
        "| Empty image cell |  | Text-only sibling remains unchanged |\n";
}
