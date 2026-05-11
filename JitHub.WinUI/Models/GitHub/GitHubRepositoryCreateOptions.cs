namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubRepositoryCreateOptions
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Homepage { get; set; }

    public bool Private { get; set; }

    public string? Visibility { get; set; }

    public bool AutoInit { get; set; }

    public string? LicenseTemplate { get; set; }

    public string? GitignoreTemplate { get; set; }

    public bool? HasIssues { get; set; }

    public bool? HasProjects { get; set; }

    public bool? HasWiki { get; set; }
}
