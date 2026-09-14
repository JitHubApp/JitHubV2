using System;
using System.IO;
using System.Text.Json;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Math;

namespace MarkdownRenderer.Sample;

// Opt-in native regression evidence, not application logging of user documents.
// Both public view types share this internal observation boundary.
#pragma warning disable MR1001
internal static class MathRegressionEvidence
{
    internal static void Attach(MarkdownRendererControl view)
    {
        string? path = Environment.GetEnvironmentVariable("MARKDOWN_RENDERER_MATH_EVIDENCE");
        if (string.IsNullOrWhiteSpace(path)) return;
        view.RenderCompleted += (_, _) =>
        {
            if (view.Document is not { } document) return;
            using var output = File.Create(path);
            using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
            json.WriteStartObject();
            json.WriteNumber("processId", Environment.ProcessId);
            json.WriteStartArray("diagnostics");
            foreach (var diagnostic in document.Diagnostics)
            {
                json.WriteStartObject();
                json.WriteString("code", diagnostic.Code);
                json.WriteString("message", diagnostic.Message);
                json.WriteNumber("start", diagnostic.SourceSpan.Start);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("displayFormulas");
            foreach (var formula in MathDelimiterScanner.Scan(document.Source).Formulas)
            {
                if (formula.DisplayMode != MathFormulaDisplayMode.Display) continue;
                json.WriteStartObject();
                json.WriteString("source", formula.TexSource);
                if (document.TryGetBlockExtensionContent(
                    new SourceSpan(formula.SourceRange.Start, formula.SourceRange.Length), out var fragment) && fragment is not null)
                {
                    json.WriteStartArray("kinds");
                    foreach (var item in fragment.Items) json.WriteStringValue(item.Kind.ToString());
                    json.WriteEndArray();
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        };
    }
}
