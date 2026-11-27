using System.Text;

namespace MarkdownRenderer.ScrollFidelityProbe.Corpus;

/// <summary>
/// A deterministic, README-shaped long document: headings, wrapped prose with
/// inline formatting, nested lists, task lists, tables, block quotes and fenced
/// code. It has no images or embeds, so every unrendered region in a captured
/// frame is the renderer's own and never a pending network resource.
/// </summary>
internal static class LongReadmeCorpus
{
    private static readonly string[] Words =
    [
        "renderer", "viewport", "native", "layout", "composition", "glyph", "surface",
        "document", "paragraph", "selection", "markdown", "measure", "scroll", "tile",
        "compositor", "thread", "budget", "reflow", "anchor", "virtualized", "accessible",
        "baseline", "DirectWrite", "Win2D", "pipeline", "snapshot", "immutable", "cache",
    ];

    internal static string Create(int targetUtf16Characters)
    {
        var builder = new StringBuilder(targetUtf16Characters + 4096);
        var random = new Random(20261125);
        builder.Append("# Scroll fidelity corpus\n\n");
        builder.Append("A long README-shaped document for measuring unrendered area during fast scrolling.\n\n");
        int section = 0;
        while (builder.Length < targetUtf16Characters)
        {
            section++;
            builder.Append("## Section ").Append(section).Append(": ").Append(Phrase(random, 3)).Append("\n\n");
            for (int paragraph = 0; paragraph < 3; paragraph++)
                AppendParagraph(builder, random);

            switch (section % 6)
            {
                case 0:
                    AppendTable(builder, random, section);
                    break;
                case 1:
                    AppendCode(builder, random, section);
                    break;
                case 2:
                    AppendList(builder, random);
                    break;
                case 3:
                    builder.Append("> ").Append(Sentence(random, 24)).Append(' ').Append(Sentence(random, 18)).Append("\n\n");
                    break;
                case 4:
                    builder.Append("### ").Append(Phrase(random, 4)).Append("\n\n");
                    AppendTaskList(builder, random);
                    break;
                default:
                    AppendParagraph(builder, random);
                    break;
            }
        }

        return builder.ToString();
    }

    private static void AppendParagraph(StringBuilder builder, Random random)
    {
        int sentences = random.Next(2, 5);
        for (int index = 0; index < sentences; index++)
        {
            builder.Append(Sentence(random, random.Next(10, 22)));
            builder.Append(index == sentences - 1 ? "\n\n" : " ");
        }
    }

    private static void AppendTable(StringBuilder builder, Random random, int section)
    {
        builder.Append("| Key | Description | Value |\n|:---|:---|---:|\n");
        for (int row = 0; row < 8; row++)
        {
            builder.Append("| `k").Append(section).Append('_').Append(row).Append("` | ")
                .Append(Phrase(random, 5)).Append(" | ").Append(random.Next(1000, 99999)).Append(" |\n");
        }

        builder.Append('\n');
    }

    private static void AppendCode(StringBuilder builder, Random random, int section)
    {
        builder.Append("```csharp\n");
        for (int line = 0; line < 12; line++)
        {
            builder.Append("var ").Append(Words[random.Next(Words.Length)]).Append(section).Append('_').Append(line)
                .Append(" = Compute(\"").Append(Phrase(random, 3)).Append("\", ").Append(random.Next(100)).Append(");\n");
        }

        builder.Append("```\n\n");
    }

    private static void AppendList(StringBuilder builder, Random random)
    {
        for (int item = 0; item < 6; item++)
        {
            builder.Append("- ").Append(Sentence(random, random.Next(6, 14))).Append('\n');
            if (item % 2 == 0)
                builder.Append("  - ").Append(Sentence(random, random.Next(5, 10))).Append('\n');
        }

        builder.Append('\n');
    }

    private static void AppendTaskList(StringBuilder builder, Random random)
    {
        for (int item = 0; item < 4; item++)
        {
            builder.Append(item % 2 == 0 ? "- [x] " : "- [ ] ").Append(Sentence(random, random.Next(5, 12))).Append('\n');
        }

        builder.Append('\n');
    }

    private static string Sentence(Random random, int words)
    {
        var builder = new StringBuilder();
        for (int index = 0; index < words; index++)
        {
            string word = Words[random.Next(Words.Length)];
            if (index == 0)
                word = char.ToUpperInvariant(word[0]) + word[1..];
            if (index > 0)
                builder.Append(' ');
            switch (random.Next(14))
            {
                case 0:
                    builder.Append("**").Append(word).Append("**");
                    break;
                case 1:
                    builder.Append('*').Append(word).Append('*');
                    break;
                case 2:
                    builder.Append('`').Append(word).Append('`');
                    break;
                case 3:
                    builder.Append('[').Append(word).Append("](https://example.com/").Append(word).Append(')');
                    break;
                default:
                    builder.Append(word);
                    break;
            }
        }

        return builder.Append('.').ToString();
    }

    private static string Phrase(Random random, int words)
    {
        var builder = new StringBuilder();
        for (int index = 0; index < words; index++)
        {
            if (index > 0)
                builder.Append(' ');
            builder.Append(Words[random.Next(Words.Length)]);
        }

        return builder.ToString();
    }
}
