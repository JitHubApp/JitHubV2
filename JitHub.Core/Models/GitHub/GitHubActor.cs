using System.Globalization;
using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubActor
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonIgnore]
    public string AutomationId => Id > 0
        ? $"GitHubActor_{Id.ToString(CultureInfo.InvariantCulture)}"
        : $"GitHubActor_{NormalizeAutomationSegment(Login)}";

    private static string NormalizeAutomationSegment(string? value)
    {
        string input = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        char[] normalized = new char[input.Length];
        for (int index = 0; index < input.Length; index++)
        {
            char character = input[index];
            normalized[index] = char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '_';
        }

        return new string(normalized);
    }
}
