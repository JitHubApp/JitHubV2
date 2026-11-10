using System.Text.Json.Serialization;

namespace JitHub.Core.Models.GitHub.Repositories;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubBranch
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
