using System.Text.Json.Serialization;

namespace JitHub.Core.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubSearchCountResponse
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("incomplete_results")]
    public bool IncompleteResults { get; set; }
}
