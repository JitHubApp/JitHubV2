using System.Text.Json.Serialization;

namespace MarkdownRenderer.PerformanceHarness.StageProfiles;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(FirstViewportStageProfileArtifact))]
internal partial class FirstViewportStageProfileJsonContext : JsonSerializerContext
{
}
