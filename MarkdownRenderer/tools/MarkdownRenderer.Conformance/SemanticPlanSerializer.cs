using System.Security.Cryptography;
using System.Text.Json;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Conformance;

public static class SemanticPlanSerializer
{
    public const int SchemaVersion = 1;

    public static byte[] Serialize(MarkdownProfile profile, MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(document);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("profile", profile.Id);
            writer.WriteNumber("sourceLengthUtf16", document.Source.Length);
            writer.WriteString("sourceSha256", Sha256(document.Source));
            WriteDiagnostics(writer, document.Diagnostics);
            WriteHeadings(writer, document.GetHeadings());
            WriteLinks(writer, document.GetLinks());
            WriteCodeBlocks(writer, document.GetCodeBlocks());
            WriteImages(writer, document.GetImages());
            WriteFootnotes(writer, document.GetFootnotes());
            WriteDefinitions(writer, document.GetDefinitionItems());
            WriteAbbreviations(writer, document.GetAbbreviations());
            WriteFragments(writer, document.GetFragments());
            WriteSourceMap(writer, document.SourceMap);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static string Sha256(ReadOnlySpan<byte> payload)
        => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static string Sha256(string value)
        => Sha256(System.Text.Encoding.UTF8.GetBytes(value));

    private static void WriteDiagnostics(Utf8JsonWriter writer, IReadOnlyList<MarkdownDiagnostic> values)
    {
        writer.WriteStartArray("diagnostics");
        foreach (var value in values)
        {
            writer.WriteStartObject();
            writer.WriteString("code", value.Code);
            writer.WriteString("severity", value.Severity.ToString());
            writer.WriteString("message", value.Message);
            WriteSpan(writer, value.SourceSpan);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteHeadings(Utf8JsonWriter writer, IReadOnlyList<MarkdownHeading> values)
    {
        writer.WriteStartArray("headings");
        foreach (var value in values)
        {
            writer.WriteStartObject();
            writer.WriteNumber("level", value.Level);
            writer.WriteString("text", value.DisplayText);
            writer.WriteNumber("block", value.BlockIndex);
            WriteSpan(writer, value.SourceSpan);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteLinks(Utf8JsonWriter writer, IReadOnlyList<MarkdownLink> values)
    {
        writer.WriteStartArray("links");
        foreach (var value in values)
        {
            writer.WriteStartObject();
            writer.WriteString("text", value.DisplayText);
            writer.WriteString("url", value.Url);
            writer.WriteString("title", value.Title);
            writer.WriteNumber("block", value.BlockIndex);
            WriteSpan(writer, value.SourceSpan);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteCodeBlocks(Utf8JsonWriter writer, IReadOnlyList<MarkdownCodeBlock> values)
    {
        writer.WriteStartArray("codeBlocks");
        foreach (var value in values)
        {
            writer.WriteStartObject();
            writer.WriteString("text", value.DisplayText);
            writer.WriteString("language", value.Language);
            writer.WriteNumber("block", value.BlockIndex);
            WriteSpan(writer, value.SourceSpan);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteImages(Utf8JsonWriter writer, IReadOnlyList<MarkdownImage> values)
    {
        writer.WriteStartArray("images");
        foreach (var value in values)
        {
            writer.WriteStartObject();
            writer.WriteString("text", value.DisplayText);
            writer.WriteString("source", value.Source);
            writer.WriteString("alt", value.AltText);
            writer.WriteString("title", value.Title);
            writer.WriteBoolean("inline", value.IsInline);
            writer.WriteNumber("block", value.BlockIndex);
            WriteSpan(writer, value.SourceSpan);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteFootnotes(Utf8JsonWriter writer, IReadOnlyList<MarkdownFootnote> values)
    {
        writer.WriteStartArray("footnotes");
        foreach (var value in values)
        {
            writer.WriteStartObject();
            writer.WriteString("label", value.Label);
            writer.WriteString("text", value.DisplayText);
            writer.WriteNumber("order", value.Order);
            writer.WriteNumber("block", value.BlockIndex);
            WriteSpan(writer, value.SourceSpan);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteDefinitions(Utf8JsonWriter writer, IReadOnlyList<MarkdownDefinitionItem> values)
    {
        writer.WriteStartArray("definitions");
        foreach (var value in values)
        {
            writer.WriteStartObject();
            writer.WriteString("term", value.Term);
            writer.WriteString("definition", value.Definition);
            writer.WriteString("marker", value.Marker.ToString());
            writer.WriteNumber("block", value.BlockIndex);
            WriteSpan(writer, value.SourceSpan);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteAbbreviations(Utf8JsonWriter writer, IReadOnlyList<MarkdownAbbreviation> values)
    {
        writer.WriteStartArray("abbreviations");
        foreach (var value in values)
        {
            writer.WriteStartObject();
            writer.WriteString("text", value.DisplayText);
            writer.WriteString("expansion", value.Expansion);
            writer.WriteNumber("block", value.BlockIndex);
            WriteSpan(writer, value.SourceSpan);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteFragments(Utf8JsonWriter writer, IReadOnlyList<MarkdownFragment> values)
    {
        writer.WriteStartArray("fragments");
        foreach (var value in values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", value.Id);
            writer.WriteNumber("block", value.BlockIndex);
            WriteSpan(writer, value.SourceSpan);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteSourceMap(Utf8JsonWriter writer, IReadOnlyList<MarkdownSourceMapEntry> values)
    {
        writer.WriteStartArray("sourceMap");
        foreach (var value in values)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", value.Kind.ToString());
            writer.WriteNumber("block", value.BlockIndex);
            WriteSpan(writer, value.SourceSpan);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteSpan(Utf8JsonWriter writer, SourceSpan span)
    {
        writer.WriteNumber("startUtf16", span.Start);
        writer.WriteNumber("lengthUtf16", span.Length);
    }
}
