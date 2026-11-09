using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubBranch
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
