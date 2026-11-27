using System.Text.Json.Serialization;

namespace MarkdownRenderer.ScrollFidelityProbe.Reports;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ProbeReport))]
internal sealed partial class ProbeReportJsonContext : JsonSerializerContext;
